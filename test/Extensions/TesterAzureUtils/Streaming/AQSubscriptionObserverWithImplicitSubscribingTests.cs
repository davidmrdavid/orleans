using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Streams;
using Orleans.TestingHost;
using Tester.StreamingTests.ProgrammaticSubscribeTests;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Tester.AzureUtils.Streaming
{
    [TestCategory("Functional")]
    public class AQSubscriptionObserverWithImplicitSubscribingTests : SubscriptionObserverWithImplicitSubscribingTestRunner, IClassFixture<AQSubscriptionObserverWithImplicitSubscribingTests.Fixture>
    {
        private const int queueCount = 8;
        public class Fixture : BaseAzureTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            }

            public override async Task DisposeAsync()
            {
                await base.DisposeAsync();
                if (!string.IsNullOrWhiteSpace(TestDefaultConfiguration.DataConnectionString))
                {
                    await AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance,
                        AzureQueueUtilities.GenerateQueueNames($"{this.HostedCluster.Options.ClusterId}{StreamProviderName}", queueCount),
                        new AzureQueueOptions().ConfigureTestDefaults());

                    await AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance,
                        AzureQueueUtilities.GenerateQueueNames($"{this.HostedCluster.Options.ClusterId}{StreamProviderName2}", queueCount),
                        new AzureQueueOptions().ConfigureTestDefaults());
                }
            }
        }

        private class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddAzureQueueStreams(StreamProviderName, sb=>
                    {
                        sb.ConfigureAzureQueue(ob => ob.Configure<IOptions<ClusterOptions>>((options, dep) =>
                        {
                            options.ConfigureTestDefaults();
                            options.QueueNames = AzureQueueUtilities.GenerateQueueNames($"{dep.Value.ClusterId}{StreamProviderName}", queueCount);
                        }));
                        sb.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                    })
                    .AddAzureQueueStreams(StreamProviderName2, sb =>
                    {
                        sb.ConfigureAzureQueue(ob => ob.Configure<IOptions<ClusterOptions>>((options, dep) =>
                        {
                            options.ConfigureTestDefaults();
                            options.QueueNames = AzureQueueUtilities.GenerateQueueNames($"{dep.Value.ClusterId}{StreamProviderName2}", queueCount);
                        }));
                        sb.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                    })
                    .AddMemoryGrainStorageAsDefault()
                    .AddMemoryGrainStorage("PubSubStore");
            }
        }

        public AQSubscriptionObserverWithImplicitSubscribingTests(ITestOutputHelper output, Fixture fixture)
            : base(fixture)
        {
            fixture.EnsurePreconditionsMet();
        }

        [SkippableFact]
        public override async Task StreamingTests_ImplicitSubscribProvider_DontHaveSubscriptionManager()
        {
            await base.StreamingTests_ImplicitSubscribProvider_DontHaveSubscriptionManager();
        }

        [SkippableFact]
        public override async Task StreamingTests_Consumer_Producer_Subscribe()
        {
            await base.StreamingTests_Consumer_Producer_Subscribe();
        }

        [SkippableFact]
        public override async Task StreamingTests_Consumer_Producer_SubscribeToTwoStream_MessageWithPolymorphism()
        {
            await base.StreamingTests_Consumer_Producer_SubscribeToTwoStream_MessageWithPolymorphism();
        }

        [SkippableFact]
        public override async Task StreamingTests_Consumer_Producer_SubscribeToStreamsHandledByDifferentStreamProvider()
        {
            await base.StreamingTests_Consumer_Producer_SubscribeToStreamsHandledByDifferentStreamProvider();
        }
    }
}
