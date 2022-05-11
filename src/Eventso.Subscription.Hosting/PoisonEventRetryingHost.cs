using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Eventso.Subscription.Configurations;
using Eventso.Subscription.Kafka;
using Eventso.Subscription.Kafka.DeadLetter;
using Eventso.Subscription.Kafka.DeadLetter.Postgres;
using Eventso.Subscription.Kafka.DeadLetter.Store;
using Eventso.Subscription.Observing.Batch;
using Eventso.Subscription.Observing.DeadLetter;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Eventso.Subscription.Hosting
{
    public sealed class PoisonEventRetryingHost : BackgroundService
    {
        private const long RetryLockId = 1; 
        
        private readonly IConnectionFactory _connectionFactory;
        private readonly ILogger _logger;
        private readonly IReadOnlyCollection<TopicRetryingService> _topicRetryingServices;

        public PoisonEventRetryingHost(
            IEnumerable<ISubscriptionCollection> subscriptions,
            IMessagePipelineFactory pipelineFactory,
            IMessageHandlersRegistry handlersRegistry,
            IPoisonEventStore poisonEventStore,
            IDeadLetterQueueScopeFactory deadLetterQueueScopeFactory,
            IConnectionFactory connectionFactory,
            ILoggerFactory loggerFactory)
        {
            _connectionFactory = connectionFactory;
            _logger = loggerFactory.CreateLogger<SubscriptionHost>();

            _topicRetryingServices = (subscriptions ?? throw new ArgumentNullException(nameof(subscriptions)))
                .SelectMany(x => x)
                .Where(x => x.EnableDeadLetterQueue)
                .Select(x => CreateTopicRetryingService(x, pipelineFactory, handlersRegistry, poisonEventStore, deadLetterQueueScopeFactory, loggerFactory))
                .ToArray();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_topicRetryingServices.Count == 0)
                return;
            
            while (!stoppingToken.IsCancellationRequested)
            {
                // TODO hide postgres behind some interface
                await using var connection = _connectionFactory.ReadWrite();
                await DistributedMonitor.TryEnter(connection, RetryLockId, stoppingToken);

                try
                {
                    foreach (var topicRetryingService in _topicRetryingServices)
                        await topicRetryingService.Retry(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Dead letter queue retrying failed.");
                }
                finally
                {
                    await DistributedMonitor.Exit(connection, RetryLockId, stoppingToken);
                }

                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); // TODO get from configuration
            }
        }

        private static TopicRetryingService CreateTopicRetryingService(
            SubscriptionConfiguration config,
            IMessagePipelineFactory messagePipelineFactory,
            IMessageHandlersRegistry handlersRegistry,
            IPoisonEventStore poisonEventStore,
            IDeadLetterQueueScopeFactory deadLetterQueueScopeFactory,
            ILoggerFactory loggerFactory)
        {
            IEventHandler<Event> eventHandler = new RetryingEventHandler(
                new Observing.EventHandler<Event>(
                    handlersRegistry,
                    messagePipelineFactory.Create(config.HandlerConfig)),
                deadLetterQueueScopeFactory,
                poisonEventStore);

            if (config.BatchProcessingRequired)
                eventHandler = config.BatchConfiguration.HandlingStrategy switch
                {
                    BatchHandlingStrategy.SingleType => eventHandler,
                    BatchHandlingStrategy.SingleTypeLastByKey => new SingleTypeLastByKeyEventHandler<Event>(eventHandler),
                    BatchHandlingStrategy.OrderedWithinKey => new OrderedWithinKeyEventHandler<Event>(eventHandler),
                    BatchHandlingStrategy.OrderedWithinType => new OrderedWithinTypeEventHandler<Event>(eventHandler),
                    _ => throw new InvalidOperationException(
                        $"Unknown handling strategy: {config.BatchConfiguration.HandlingStrategy}")
                };

            return new TopicRetryingService(
                config.Settings.Topic,
                poisonEventStore,
                new ValueObjectDeserializer(config.Serializer, handlersRegistry),
                eventHandler,
                loggerFactory.CreateLogger<TopicRetryingService>());
        }
    }
}