using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Eventso.Subscription.Kafka.DeadLetter.Store;
using Eventso.Subscription.Observing.DeadLetter;
using Microsoft.Extensions.Logging;

namespace Eventso.Subscription.Kafka.DeadLetter
{
    public sealed class PoisonEventInbox : IPoisonEventInbox<Event>, IDisposable
    {
        private readonly IPoisonEventStore _eventStore;
        private readonly IConsumer<Guid, byte[]> _deadMessageConsumer;

        public PoisonEventInbox(
            IPoisonEventStore eventStore,
            ConsumerSettings settings,
            ILogger<PoisonEventInbox> logger)
        {
            _eventStore = eventStore;

            if (string.IsNullOrWhiteSpace(settings.Config.BootstrapServers))
                throw new InvalidOperationException("Brokers are not specified.");

            if (string.IsNullOrEmpty(settings.Config.GroupId))
                throw new InvalidOperationException("Group Id is not specified.");

            var config = new ConsumerConfig(settings.Config.ToDictionary(e => e.Key, e => e.Value))
            {
                EnableAutoCommit = false,
                EnableAutoOffsetStore = false,
                AutoOffsetReset = AutoOffsetReset.Error,
                AllowAutoCreateTopics = false,
                GroupId = settings.Config.GroupId + "_cemetery" // boo!
            };
            _deadMessageConsumer = new ConsumerBuilder<Guid, byte[]>(config)
                .SetKeyDeserializer(KeyGuidDeserializer.Instance)
                .SetValueDeserializer(Deserializers.ByteArray)
                .SetErrorHandler((_, e) => logger.LogError(
                    $"{nameof(PoisonEventInbox)} internal error: Topic: {settings.Topic}, {e.Reason}, Fatal={e.IsFatal}," +
                    $" IsLocal= {e.IsLocalError}, IsBroker={e.IsBrokerError}"))
                .Build();
        }

        public async Task Add(IReadOnlyCollection<PoisonEvent<Event>> events, CancellationToken cancellationToken)
        {
            if (events.Count == 0)
                return;

            var inboxThresholdChecker = new InboxThresholdChecker(_eventStore);
            var openingPoisonEvents = new PooledList<OpeningPoisonEvent>(events.Count);
            foreach (var @event in events)
            {
                await inboxThresholdChecker.EnsureThreshold(@event.Event.Topic, cancellationToken);
                openingPoisonEvents.Add(CreateOpeningPoisonEvent(@event, cancellationToken));
            }

            await _eventStore.Add(DateTime.UtcNow, openingPoisonEvents, cancellationToken);
        }

        public Task<bool> IsPartOfPoisonStream(Event @event, CancellationToken cancellationToken)
            => _eventStore.IsStreamStored(@event.Topic, @event.GetKey(), cancellationToken);

        public async Task<IPoisonStreamCollection<Event>> GetPoisonStreams(
            IReadOnlyCollection<Event> events,
            CancellationToken cancellationToken)
        {
            using var streamIds = new PooledList<StreamId>(events.Count);
            foreach (var @event in events)
                streamIds.Add(new StreamId(@event.Topic, @event.GetKey()));
            
            HashSet<StreamId> poisonStreamIds = null;
            await foreach (var streamId in _eventStore.GetStoredStreams(streamIds, cancellationToken))
            {
                poisonStreamIds ??= new HashSet<StreamId>();
                poisonStreamIds.Add(streamId);
            }

            return poisonStreamIds != null
                ? new PoisonStreamCollection(poisonStreamIds)
                : null;
        }

        public void Dispose()
        {
            _deadMessageConsumer.Close();
            _deadMessageConsumer.Dispose();
        }

        private OpeningPoisonEvent CreateOpeningPoisonEvent(
            PoisonEvent<Event> @event,
            CancellationToken cancellationToken)
        {
            var topicPartitionOffset = @event.Event.GetTopicPartitionOffset();

            var rawEvent = Consume(topicPartitionOffset, cancellationToken);

            return new OpeningPoisonEvent(
                topicPartitionOffset,
                rawEvent.Message.Key,
                rawEvent.Message.Value,
                rawEvent.Message.Timestamp.UtcDateTime,
                rawEvent.Message
                    .Headers
                    .Select(c => new EventHeader(c.Key, c.GetValueBytes()))
                    .ToArray(),
                @event.Reason);
        }

        private ConsumeResult<Guid, byte[]> Consume(
            TopicPartitionOffset topicPartitionOffset,
            CancellationToken cancellationToken)
        {
            try
            {
                // one per observer (so no concurrency should exist) 
                _deadMessageConsumer.Assign(topicPartitionOffset);

                var rawEvent = _deadMessageConsumer.Consume(cancellationToken);
                if (!rawEvent.TopicPartitionOffset.Equals(topicPartitionOffset))
                    throw new EventHandlingException(
                        topicPartitionOffset.ToString(),
                        "Consumed message offset doesn't match requested one.",
                        null);

                return rawEvent;
            }
            finally
            {
                _deadMessageConsumer.Unassign();
            }
        }

        [StructLayout(LayoutKind.Auto)]
        private struct InboxThresholdChecker
        {
            // TODO const -> settings
            private const int MaxNumberOfPoisonedEventsInTopic = 1000;

            private readonly IPoisonEventStore _eventStore;
            private string _singleTopic;
            private List<string> _topics;

            public InboxThresholdChecker(IPoisonEventStore eventStore)
            {
                _eventStore = eventStore;
                _singleTopic = null;
                _topics = null;
            }

            public async ValueTask EnsureThreshold(string topic, CancellationToken cancellationToken)
            {
                if (_singleTopic == topic)
                    return;

                if (_topics?.Contains(topic) == true)
                    return;

                var alreadyPoisoned = await _eventStore.Count(topic, cancellationToken);
                if (alreadyPoisoned >= MaxNumberOfPoisonedEventsInTopic)
                    throw new EventHandlingException(
                        topic,
                        $"Dead letter queue exceeds {MaxNumberOfPoisonedEventsInTopic} size.",
                        null);

                if (_singleTopic == null)
                {
                    _singleTopic = topic;
                    return;
                }

                _topics ??= new List<string>(1);
                _topics.Add(topic);
            }
        }
        
        private sealed class PoisonStreamCollection : IPoisonStreamCollection<Event>
        {
            private readonly HashSet<StreamId> _poisonStreamIds;

            public PoisonStreamCollection(HashSet<StreamId> poisonStreamIds)
                => _poisonStreamIds = poisonStreamIds;

            public bool IsPartOfPoisonStream(Event @event)
                => _poisonStreamIds.Contains(new StreamId(@event.Topic, @event.GetKey()));
        }
    }
}