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
using System.Text.Json;

namespace RaptBrewfather
{
    public class BearerTokenResponse {
        public string access_token { get; set; }
        public int expires_in { get; set; }
        public string token_type { get; set; }
        public string scope { get; set; }
    }
    public static class SendTelemetry
    {
        static readonly HttpClient httpClient = new HttpClient();

        [FunctionName("SendTelemetry")]
        public static async Task<List<string>> RunOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context)
        {
            var outputs = new List<string>();
            await context.CallActivityAsync<bool>("CheckRaptBearerToken", null);
            return outputs;
        }

        [FunctionName("CheckRaptBearerToken")]
        public static async Task<bool> CheckRaptBearerToken(ILogger log) {
            string keyVaultUri = System.Environment.GetEnvironmentVariable("KeyVaultUri", System.EnvironmentVariableTarget.Process);

            var secretClient = new SecretClient(new System.Uri(keyVaultUri), new DefaultAzureCredential());
            KeyVaultSecret accessToken = secretClient.GetSecret("accesstoken");
            if (accessToken.Properties.ExpiresOn < System.DateTimeOffset.UtcNow) {
                try {
                    // Create the body of the request
                    var data = new List<KeyValuePair<string, string>>();
                    data.Add(new KeyValuePair<string, string>("client_id", "rapt-user"));
                    data.Add(new KeyValuePair<string, string>("grant_type","password"));
                    data.Add(new KeyValuePair<string, string>("username",System.Environment.GetEnvironmentVariable("RaptUsername", System.EnvironmentVariableTarget.Process)));
                    data.Add(new KeyValuePair<string, string>("password",System.Environment.GetEnvironmentVariable("RaptApiKey", System.EnvironmentVariableTarget.Process)));

                    var req = new HttpRequestMessage(HttpMethod.Post, "https://id.rapt.io/connect/token"){
                        Content = new FormUrlEncodedContent(data)
                    };

                    var res = await httpClient.SendAsync(req);
                    string response = await res.Content.ReadAsStringAsync();

                    // Parse the JSON response
                    BearerTokenResponse bearerTokenResponse = JsonSerializer.Deserialize<BearerTokenResponse>(response);

                    // Create a new version of the secret
                    KeyVaultSecret updatedSecret = new KeyVaultSecret("accesstoken", bearerTokenResponse.access_token);
                    updatedSecret.Properties.ExpiresOn = System.DateTimeOffset.UtcNow.AddSeconds(bearerTokenResponse.expires_in);

                    // Commit the new version of the secret
                    KeyVaultSecret update = secretClient.SetSecret(updatedSecret);
                }
                catch {
                    // ok
                }
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