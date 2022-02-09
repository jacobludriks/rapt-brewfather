using System;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Azure.Security.KeyVault.Secrets;
using Azure.Identity;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Azure.WebJobs.Extensions.Http;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Linq;

namespace RaptBrewfather
{
    public class SendTelemetry
    {
        static HttpClient _httpClient = new HttpClient();

        [FunctionName("SendTelemetry")]
        public async Task Run([TimerTrigger("0 */20 * * * *")]TimerInfo myTimer, ILogger log)
        {
            string bearerToken = await GetRaptBearerToken(_httpClient, log);
            var hydrometers = await GetHydrometers(_httpClient, bearerToken, log);
            string brewfatherUri = System.Environment.GetEnvironmentVariable("BrewfatherUri", System.EnvironmentVariableTarget.Process);
            foreach (var hydrometer in hydrometers) {
                var telemetry = await GetHydrometerTelemetry(_httpClient, bearerToken, hydrometer, log);
                await PublishBrewfatherTelemetry(_httpClient, brewfatherUri, telemetry, log);
            }

            log.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");
        }

        public static async Task<string> GetRaptBearerToken(HttpClient httpClient, ILogger log) {
            var env = System.Environment.GetEnvironmentVariable("AZURE_FUNCTIONS_ENVIRONMENT", System.EnvironmentVariableTarget.Process);

            if (env == "Production") {
                // Get the Azure Key Vault URI from the application settings on the Azure Function
                string keyVaultUri = System.Environment.GetEnvironmentVariable("KeyVaultUri", System.EnvironmentVariableTarget.Process);

                // Create a new SecretClient using the Managed Service Identity of the Azure Function, then retrieve the bearer token for the RAPT API
                SecretClient secretClient = new SecretClient(new System.Uri(keyVaultUri), new DefaultAzureCredential());
                KeyVaultSecret accessToken = secretClient.GetSecret("accesstoken");

                // Set the secret to expired if
                if (accessToken.Properties.ExpiresOn < System.DateTimeOffset.UtcNow == false) {
                    return accessToken.Value;
                }
            }
            
            // If the bearer token lifetime has elapsed, retrieve a new bearer token
            log.LogInformation("Bearer token has expired, retrieving new token");
            try {
                // Create the body of the request for a new bearer token
                var data = new List<KeyValuePair<string, string>>();
                data.Add(new KeyValuePair<string, string>("client_id", "rapt-user"));
                data.Add(new KeyValuePair<string, string>("grant_type","password"));
                data.Add(new KeyValuePair<string, string>("username",System.Environment.GetEnvironmentVariable("RaptUsername", System.EnvironmentVariableTarget.Process)));
                data.Add(new KeyValuePair<string, string>("password",System.Environment.GetEnvironmentVariable("RaptApiKey", System.EnvironmentVariableTarget.Process)));

                // Create the request
                var req = new HttpRequestMessage(HttpMethod.Post, "https://id.rapt.io/connect/token"){
                    Content = new FormUrlEncodedContent(data)
                };

                // Send the request to the RAPT authentication provider
                using (var res = await httpClient.SendAsync(req)) {
                    if (res.Content == null) {
                        // Error out
                    }
                    string response = await res.Content.ReadAsStringAsync();

                    // Parse the JSON response
                    BearerTokenResponse bearerTokenResponse = JsonSerializer.Deserialize<BearerTokenResponse>(response);
                    if (bearerTokenResponse == null) {
                        // Do something
                    }

                    if (env == "Production") {
                        // Create a new version of the bearer token secret
                        KeyVaultSecret updatedSecret = new KeyVaultSecret("accesstoken", bearerTokenResponse.access_token);
                        updatedSecret.Properties.ExpiresOn = System.DateTimeOffset.UtcNow.AddSeconds(bearerTokenResponse.expires_in);

                        // Get the Azure Key Vault URI from the application settings on the Azure Function
                        string keyVaultUri = System.Environment.GetEnvironmentVariable("KeyVaultUri", System.EnvironmentVariableTarget.Process);

                        // Create a new SecretClient using the Managed Service Identity of the Azure Function, then retrieve the bearer token for the RAPT API
                        SecretClient secretClient = new SecretClient(new System.Uri(keyVaultUri), new DefaultAzureCredential());

                        // Commit the new version of the bearer token secret
                        KeyVaultSecret update = secretClient.SetSecret(updatedSecret);
                    }

                    // Return the new bearer token
                    return bearerTokenResponse.access_token;
                }
            }
            catch (HttpRequestException exception) {
                log.LogError(exception, "It broked");
                // We should probably retry
            }
            catch (Exception exception) {
                log.LogError(exception, "Unhandled exception");
            }
            return "ok";
        }
        public static async Task<IEnumerable<string>> GetHydrometers(HttpClient httpClient, string bearerToken, ILogger log) {
            // Set the URI of the GetHydrometers RAPT API endpoint
            string requestUri = "https://api.rapt.io/api/Hydrometers/GetHydrometers";

            // Create the request
            var req = new HttpRequestMessage(HttpMethod.Get, requestUri) {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", bearerToken) }
            };

            // Send the request to the RAPT API
            using (var res = await httpClient.SendAsync(req)) {
                string response = await res.Content.ReadAsStringAsync();
                
                // Parse the JSON response
                var jsonResponse = JsonDocument.Parse(response);

                // Get the ID of each hydrometer from the JSON response
                var hydrometers = jsonResponse.RootElement
                                    .EnumerateArray()
                                    .SelectMany(o => o.EnumerateObject())
                                    .Where(n => n.Name.Equals("id") && n.Value.ValueKind == JsonValueKind.String)
                                    .Select(s => s.Value.ToString());

                // Return the list of hydrometer ID's
                return hydrometers;
            }
        }

        public static async Task<BrewfatherStream> GetHydrometerTelemetry(HttpClient httpClient, string bearerToken, string hydrometerId, ILogger log) {
            // Set two DateTime objects - now, and one hour ago. Both are in UTC.
            // The DateTime objects should be in the "s" format, ie. 2022-02-09T08:23:32.165
            DateTime now = DateTime.UtcNow;
            DateTime previous = now.AddHours(-1);

            // Create the body of the request for the hydrometer telemetry data
            var data = new List<KeyValuePair<string, string>>();
            data.Add(new KeyValuePair<string, string>("hydrometerId", hydrometerId));
            data.Add(new KeyValuePair<string, string>("startDate", previous.ToString("s")));
            data.Add(new KeyValuePair<string, string>("endDate", now.ToString("s")));

            // Create the request
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.rapt.io/api/Hydrometers/GetTelemetry"){
                Content = new FormUrlEncodedContent(data),
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", bearerToken) }
            };

            // Send the request to the RAPT API
            using (var res = await httpClient.SendAsync(req)) {
                string response = await res.Content.ReadAsStringAsync();

                // Parse the telemetry data from the API response
                var telemetry = JsonSerializer.Deserialize<List<RaptTelemetry>>(response);

                // Select only the most recent telemetry from the hydrometer
                var recentResult = telemetry.OrderByDescending(t => t.createdOn).First();

                // Convert the RAPT API object to a Brewfather API object
                var brewfather = new BrewfatherStream(){
                    name = hydrometerId,
                    temp = recentResult.temperature,
                    temp_unit = "C",
                    gravity = Math.Round(recentResult.gravity / 1000, 3),
                    gravity_unit = "G"
                };

                // Return the Brewfather API object
                return brewfather;
            }
        }

        public static async Task<bool> PublishBrewfatherTelemetry(HttpClient httpClient, string brewfatherUri, BrewfatherStream stream, ILogger log) {
            // Send the Brewfather API object to the Brewfather API
            using (var res = await httpClient.PostAsJsonAsync<BrewfatherStream>(brewfatherUri, stream)) {
                string response = await res.Content.ReadAsStringAsync();
                return true;
            }
        }
    }

    public class BearerTokenResponse
    {
        public string access_token { get; set; }
        public int expires_in { get; set; }
        public string token_type { get; set; }
        public string scope { get; set; }
    }

    public class BrewfatherStream
    {
        // We do not need most of the fields that the Brewfather API can ingest
        // We only care about the temperature and gravity. Name is mandatory, and will be set to the hydrometer ID from the RAPT API
        public string name { get; set; }
        public double temp { get; set; }
        public string temp_unit { get; set; }
        public double gravity { get; set; }
        public string gravity_unit { get; set; }
    }

    public class RaptTelemetry
    {
        public string id { get; set; }
        public DateTime createdOn { get; set; }
        public double temperature { get; set; }
        public double gravity { get; set; }
        public int rssi { get; set; }
        public double battery { get; set; }
        public string version { get; set; }
    }
}
