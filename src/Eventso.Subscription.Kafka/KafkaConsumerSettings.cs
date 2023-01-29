using Confluent.Kafka;

namespace Eventso.Subscription.Kafka;

public class KafkaConsumerSettings
{
    public KafkaConsumerSettings()
    {
        Config = new ConsumerConfig
        {
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
        };
    }

    public KafkaConsumerSettings(
        string brokers,
        string groupId,
        TimeSpan? maxPollInterval = default,
        TimeSpan? sessionTimeout = default,
        AutoOffsetReset autoOffsetReset = AutoOffsetReset.Earliest,
        PartitionAssignmentStrategy assignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky,
        string groupInstanceId = null)
        : this()
    {
        Config.BootstrapServers = brokers;
        Config.GroupId = groupId;
        Config.AutoOffsetReset = autoOffsetReset;
        Config.PartitionAssignmentStrategy = assignmentStrategy;

        if (groupInstanceId != null)
            Config.GroupInstanceId = groupInstanceId;

        if (maxPollInterval.HasValue)
            Config.MaxPollIntervalMs = (int)maxPollInterval.Value.TotalMilliseconds;

        if (sessionTimeout.HasValue)
            Config.SessionTimeoutMs = (int)sessionTimeout.Value.TotalMilliseconds;
    }

    public ConsumerConfig Config { get; }
}