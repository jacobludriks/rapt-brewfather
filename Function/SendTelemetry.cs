using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Azure.Security.KeyVault.Secrets;
using Azure.Identity;

namespace RaptBrewfather
{
    public static class SendTelemetry
    {
        [FunctionName("SendTelemetry")]
        public static async Task<List<string>> RunOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context)
        {
            var outputs = new List<string>();
            await context.CallActivityAsync<bool>("CheckRaptBearerToken", null);
            return outputs;
        }

        [FunctionName("CheckRaptBearerToken")]
        public static bool CheckRaptBearerToken(ILogger log) {
            string keyVaultUri = System.Environment.GetEnvironmentVariable("KeyVaultUri", System.EnvironmentVariableTarget.Process);

            var secretClient = new SecretClient(new System.Uri(keyVaultUri), new DefaultAzureCredential());
            KeyVaultSecret accessToken = secretClient.GetSecret("accesstoken");
            if (accessToken.Properties.ExpiresOn < System.DateTimeOffset.UtcNow) {
                // Get a new access token
                return true;
            }
            else {
                return true;
            }
        } 

        [FunctionName("SendTelemetry_Hello")]
        public static string SayHello([ActivityTrigger] string name, ILogger log)
        {
            log.LogInformation($"Saying hello to {name}.");
            return $"Hello {name}!";
        }

        [FunctionName("SendTelemetry_TimerStart")]
        public static async Task RunScheduled(
            [TimerTrigger("0 */20 * * * *")] TimerInfo timerInfo,
            [DurableClient] IDurableOrchestrationClient starter,
            ILogger log)
        {
            // Function input comes from the request content.
            string instanceId = await starter.StartNewAsync("SendTelemetry", null);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");
        }
    }
}