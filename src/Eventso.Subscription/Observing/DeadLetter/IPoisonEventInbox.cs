using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Eventso.Subscription.Observing.DeadLetter
{
    public interface IPoisonEventInbox<TEvent>
        where TEvent : IEvent
    {
        Task Add(PoisonEvent<TEvent> @event, CancellationToken cancellationToken);
        Task Add(IReadOnlyCollection<PoisonEvent<TEvent>> events, CancellationToken cancellationToken);

        Task<bool> Contains(string topic, Guid key, CancellationToken cancellationToken);
        Task<IReadOnlySet<Guid>> GetContainedKeys(string topic,
            IReadOnlyCollection<Guid> keys,
            CancellationToken cancellationToken);
    }
}