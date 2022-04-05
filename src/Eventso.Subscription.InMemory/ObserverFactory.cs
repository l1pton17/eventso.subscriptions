using System;
using Eventso.Subscription.Configurations;
using Eventso.Subscription.InMemory.Hosting;
using Eventso.Subscription.Observing;
using Eventso.Subscription.Observing.Batch;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eventso.Subscription.InMemory
{
    public sealed class ObserverFactory : IObserverFactory
    {
        private readonly SubscriptionConfiguration _configuration;
        private readonly IMessagePipelineFactory _messagePipelineFactory;
        private readonly IMessageHandlersRegistry _messageHandlersRegistry;

        public ObserverFactory(
            SubscriptionConfiguration configuration,
            IMessagePipelineFactory messagePipelineFactory,
            IMessageHandlersRegistry messageHandlersRegistry)
        {
            _configuration = configuration;
            _messagePipelineFactory = messagePipelineFactory;
            _messageHandlersRegistry = messageHandlersRegistry;
        }

        public IObserver<TEvent> Create<TEvent>(IConsumer<TEvent> consumer)
            where TEvent : IEvent
        {
            var eventHandler = new Observing.EventHandler<TEvent>(
                _messageHandlersRegistry,
                _messagePipelineFactory.Create(_configuration.HandlerConfiguration));

            if (_configuration.BatchProcessingRequired)
            {
                return new BatchEventObserver<TEvent>(
                    _configuration.Topic,
                    _configuration.BatchConfiguration,
                    GetBatchHandler(),
                    consumer,
                    _messageHandlersRegistry,
                    skipUnknown: true);
            }

            return new EventObserver<TEvent>(
                _configuration.Topic,
                eventHandler,
                consumer,
                _messageHandlersRegistry,
                skipUnknown: true,
                _configuration.DeferredAckConfiguration,
                NullLogger<EventObserver<TEvent>>.Instance);

            IEventHandler<TEvent> GetBatchHandler()
            {
                return _configuration.BatchConfiguration.HandlingStrategy switch
                {
                    BatchHandlingStrategy.SingleType
                        => eventHandler,
                    BatchHandlingStrategy.SingleTypeLastByKey
                        => new SingleTypeLastByKeyEventHandler<TEvent>(eventHandler),
                    BatchHandlingStrategy.OrderedWithinKey
                        => new OrderedWithinKeyEventHandler<TEvent>(eventHandler),
                    BatchHandlingStrategy.OrderedWithinType =>
                        new OrderedWithinTypeEventHandler<TEvent>(eventHandler),
                    _ => throw new InvalidOperationException(
                        $"Unknown handling strategy: {_configuration.BatchConfiguration.HandlingStrategy}")
                };
            }
        }
    }
}