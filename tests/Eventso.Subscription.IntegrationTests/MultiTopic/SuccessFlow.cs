﻿using Eventso.Subscription.Hosting;

namespace Eventso.Subscription.IntegrationTests.MultiTopic;

public sealed class SuccessFlow : IAsyncLifetime
{
    private readonly KafkaConfig _config;
    private readonly TopicSource _topicSource;
    private readonly TestHostStartup _hostStartup;
    private readonly IFixture _fixture;

    public SuccessFlow(
        KafkaConfig config,
        TopicSource topicSource,
        TestHostStartup hostRunner,
        IFixture fixture)
    {
        _config = config;
        _topicSource = topicSource;
        _hostStartup = hostRunner;
        _fixture = fixture;
    }

    [Fact]
    public async Task MixedTypes()
    {
        const int messageCount = 100;
        var topics = await _topicSource.CreateTopics(_fixture, messageCount);

        await using var host = await _hostStartup
            .CreateServiceCollection()
            .AddSubscriptions((s, _) =>
                s.AddMultiTopic(
                    _config,
                    c => c
                        .AddJson<RedMessage>(topics.Red.Topic, bufferSize: 0)
                        .AddJson<GreenMessage>(topics.Green.Topic, bufferSize: 10)
                        .AddBatchJson<BlueMessage>(topics.Blue.Topic)
                        .AddBatchJson<BlackMessage>(topics.Black.Topic)))
            .RunHost();

        var messageHandler = host.GetHandler();

        await host.WhenAll(
            messageHandler.Red.WaitUntil(messageCount),
            messageHandler.Green.WaitUntil(messageCount),
            messageHandler.Blue.WaitUntil(messageCount * 2),
            messageHandler.Black.WaitUntil(messageCount));

        messageHandler.Red.Should().HaveCount(messageCount);
        messageHandler.Green.Should().HaveCount(messageCount);
        messageHandler.Blue.Should().HaveCount(messageCount * 2);
        messageHandler.Black.Should().HaveCount(messageCount);
    }

    [Fact]
    public async Task SingleTopic_Batch()
    {
        const int messageCount = 100;
        var (topic, messages) = await _topicSource.CreateTopicWithMessages<BlackMessage>(_fixture, messageCount);

        await using var host = await _hostStartup
            .CreateServiceCollection()
            .AddSubscriptions((s, _) =>
                s.AddMultiTopic(
                    _config,
                    c => c
                        .AddBatchJson<BlackMessage>(topic)))
            .RunHost();

        var messageHandler = host.GetHandler();

        await host.WhenAll(messageHandler.Black.WaitUntil(messageCount));

        messageHandler.Black.Should().HaveCount(messageCount);
    }


    [Fact]
    public async Task SingleTopic_Single()
    {
        const int messageCount = 100;
        var (topic, messages) = await _topicSource.CreateTopicWithMessages<RedMessage>(_fixture, messageCount);

        await using var host = await _hostStartup
            .CreateServiceCollection()
            .AddSubscriptions((s, _) =>
                s.AddMultiTopic(
                    _config,
                    c => c
                        .AddJson<RedMessage>(topic, bufferSize: 20)))
            .RunHost();

        var messageHandler = host.GetHandler();

        await host.WhenAll(messageHandler.Red.WaitUntil(messageCount));

        messageHandler.Red.Should().HaveCount(messageCount);
    }

    public Task InitializeAsync()
        => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _topicSource.DisposeAsync();
    }
}