﻿using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Confluent.Kafka;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Polly;
using Polly.Retry;

namespace Liyanjie.EventBus
{
    /// <summary>
    /// 
    /// </summary>
    public class KafkaEventBus : IEventBus, IDisposable
    {
        readonly ILogger<KafkaEventBus> logger;
        readonly KafkaSettings settings;
        readonly ISubscriptionsManager subscriptionsManager;
        readonly IServiceProvider serviceProvider;
        readonly AsyncRetryPolicy policy;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="options"></param>
        /// <param name="subscriptionsManager"></param>
        /// <param name="serviceProvider"></param>
        public KafkaEventBus(
            ILogger<KafkaEventBus> logger,
            IOptions<KafkaSettings> options,
            ISubscriptionsManager subscriptionsManager,
            IServiceProvider serviceProvider)
        {
            this.logger = logger;
            this.settings = options.Value;
            this.subscriptionsManager = subscriptionsManager ?? new InMemorySubscriptionsManager();
            this.serviceProvider = serviceProvider;
            this.policy = Policy
                .Handle<ProduceException<Guid, object>>()
                .WaitAndRetryAsync(settings.RetryCountWhenPublishEvent, retryAttempt => TimeSpan.FromSeconds(1), (exception, time) =>
                {
                    logger.LogWarning(exception.ToString());
                });
            this.subscriptionsManager.OnEventRemoved += SubscriptionsManager_OnEventRemoved;

            DoConsume();
        }

        string GetTopic<TEvent>() => typeof(TEvent).Name;

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <typeparam name="TEventHandler"></typeparam>
        public void RegisterEventHandler<TEvent, TEventHandler>()
            where TEventHandler : IEventHandler<TEvent>
        {
            subscriptionsManager.AddSubscription<TEvent, TEventHandler>();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <typeparam name="TEventHandler"></typeparam>
        public void RemoveEventHandler<TEvent, TEventHandler>()
            where TEventHandler : IEventHandler<TEvent>
        {
            subscriptionsManager.RemoveSubscription<TEvent, TEventHandler>();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<bool> PublishEventAsync<TEvent>(
            TEvent @event,
            CancellationToken cancellationToken = default)
        {
            using var producer = new ProducerBuilder<Guid, string>(settings.ProducerConfig).Build();
            var result = await policy.ExecuteAsync(async () => await producer.ProduceAsync(GetTopic<TEvent>(), new Message<Guid, string>
            {
                Key = Guid.NewGuid(),
                Value = JsonSerializer.Serialize(@event),
            }, cancellationToken));
            if (result.Status == PersistenceStatus.Persisted)
            {
                logger.LogInformation($"Publish event success,status:{result.Status},offset:{result.Offset}");

                if (task == null)
                    DoConsume();

                return true;
            }
            else
            {
                logger.LogError($"Publish event failed,status:{result.Status},message:{result.Message}");
                return false;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public void Dispose()
        {
            tokenSource?.Cancel();
            subscriptionsManager.Clear();
            task?.Dispose();
            task = null;
        }

        CancellationTokenSource tokenSource;
        Task task;
        void DoConsume()
        {
            tokenSource = new CancellationTokenSource();
            task = Task.Factory.StartNew(async token =>
            {
                var cancellationToken = (CancellationToken)token;

                using var consumer = new ConsumerBuilder<Guid, string>(settings.ConsumerConfig).Build();
                consumer.Subscribe(subscriptionsManager.GetEventNames());

                while (!cancellationToken.IsCancellationRequested)
                {
                    ConsumeResult<Guid, string> result = default;
                    try
                    {
                        result = consumer.Consume(cancellationToken);
                    }
                    catch (ConsumeException e)
                    {
                        logger.LogError($"Error occured: {e.Error.Reason}");
                        continue;
                    }

                    logger.LogInformation($"Consumed message '{result.Message}' at '{result.TopicPartitionOffset}'.");

                    var eventName = result.Topic;
                    var eventMessage = result.Message.Value;
                    await ProcessEventAsync(eventName, eventMessage);
                }
            }, tokenSource.Token);
        }
        async Task ProcessEventAsync(string eventName, string eventMessage)
        {
            if (!subscriptionsManager.HasSubscriptions(eventName))
                return;

            var eventType = subscriptionsManager.GetEventType(eventName);
            var @event = JsonSerializer.Deserialize(eventMessage, eventType);
            var handlerMethod = typeof(IEventHandler<>).MakeGenericType(eventType).GetMethod(nameof(IEventHandler<object>.HandleAsync));

            var handlerTypes = subscriptionsManager.GetEventHandlerTypes(eventName);
            using var scope = serviceProvider.CreateScope();
            foreach (var handlerType in handlerTypes)
            {
                try
                {
                    var handler = ActivatorUtilities.GetServiceOrCreateInstance(scope.ServiceProvider, handlerType);
                    await (Task)handlerMethod.Invoke(handler, new[] { @event });
                    logger.LogDebug($"{handlerType.FullName}=>{eventMessage}");
                }
                catch (Exception ex)
                {
                    logger.LogError($"{ex.Message} in {handlerType.FullName}=>{eventMessage}");
                }
            }
        }
        void SubscriptionsManager_OnEventRemoved(object sender, string eventName)
        {
            if (subscriptionsManager.IsEmpty)
            {
                task?.Dispose();
                task = null;
            }
        }
    }
}
