using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Providers.Streams.PersistentStreams;
using Orleans.Serialization;
using Orleans.Streaming.AzureStorage;
using Orleans.Streams;
using TestExtensions;

namespace Tester.TestStreamProviders
{
    public class TestAzureTableStorageStreamFailureHandler : AzureTableStorageStreamFailureHandler<StreamDeliveryFailureEntity>
    {
        private const string TableName = "TestStreamFailures";
        private const string DeploymentId = "TestDeployment";
        private TestAzureTableStorageStreamFailureHandler(SerializationManager serializationManager)
            : base(serializationManager, NullLoggerFactory.Instance, false, DeploymentId, GetStreamingAzureStorageOperationOptions())
        {
        }

        public static async Task<IStreamFailureHandler> Create(SerializationManager serializationManager)
        {
            var failureHandler = new TestAzureTableStorageStreamFailureHandler(serializationManager);
            await failureHandler.InitAsync();
            return failureHandler;
        }

        public static async Task<int> GetDeliveryFailureCount(string streamProviderName)
        {
            var dataManager = GetDataManager();
            await dataManager.InitTableAsync();

            var deliveryErrors =
                await dataManager.ReadAllTableEntriesForPartitionAsync(
                        StreamDeliveryFailureEntity.MakeDefaultPartitionKey(streamProviderName, DeploymentId));
            return deliveryErrors.Count;
        }

        private static AzureTableDataManager<TableEntity> GetDataManager()
        {
            var options = GetAzureStorageOperationOptions();
            return new AzureTableDataManager<TableEntity>(options, NullLogger.Instance);
        }

        private static AzureStorageOperationOptions GetAzureStorageOperationOptions()
        {
            var options = new AzureStorageOperationOptions { TableName = TableName };
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.ConfigureTableServiceClient(TestDefaultConfiguration.TableEndpoint, new DefaultAzureCredential());
            }
            else
            {
                options.ConfigureTableServiceClient(TestDefaultConfiguration.DataConnectionString);
            }

            return options;
        }

        private static Orleans.Streaming.AzureStorage.AzureStorageOperationOptions GetStreamingAzureStorageOperationOptions()
        {
            var options = new Orleans.Streaming.AzureStorage.AzureStorageOperationOptions { TableName = TableName };
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.ConfigureTableServiceClient(TestDefaultConfiguration.TableEndpoint, new DefaultAzureCredential());
            }
            else
            {
                options.ConfigureTableServiceClient(TestDefaultConfiguration.DataConnectionString);
            }

            return options;
        }
    }
}
