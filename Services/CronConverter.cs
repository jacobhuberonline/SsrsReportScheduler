using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using SsrsReportScheduler.Options;
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace SsrsReportScheduler.Services
{
    public class CronConverter
    {
        private readonly string _apiKey;
        private readonly string _regionEndpoint;
        private readonly string _modelId;

        public CronConverter(IOptions<BedrockOptions> options)
        {
            _apiKey = options.Value.ApiKey ?? throw new ArgumentException("API Key is missing.");
            _regionEndpoint = $"https://bedrock-runtime.{options.Value.Region}.amazonaws.com";
            _modelId = options.Value.ModelId ?? throw new ArgumentException("Model ID is missing.");
        }

        public async Task<string> ConvertDescriptionToCronAsync(string description)
        {
            var prompt = $"Convert the following user description into a valid Quartz cron expression without explanation:\n{description}";

            var client = new HttpClient();

            var cronRequest = new CronRequest
            {
                messages = new List<Message>
                {
                    new Message { role = "user", content = prompt },
                    new Message { role = "system", content = "Ensure that the cron expression is for a valid job schedule." }
                }
            };

            var requestBody = JsonConvert.SerializeObject(cronRequest);

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_regionEndpoint}/model/{_modelId}/invoke")
            {
                Content = new StringContent(requestBody, Encoding.UTF8, "application/json")  // Correct content type
            };

            request.Headers.Add("Authorization", $"Bearer {_apiKey}");
            request.Headers.Add("X-Amzn-Bedrock-Trace", "DISABLED");

            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            var response = await client.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Error: {response.StatusCode} - {errorContent}");
                throw new Exception($"Error calling API: {response.StatusCode}");
            }

            var responseString = await response.Content.ReadAsStringAsync();

            var cronExpression = ExtractCronExpressionFromResponse(responseString);
            return cronExpression;
        }

        private string ExtractCronExpressionFromResponse(string responseString)
        {
            try
            {
                var responseJson = Newtonsoft.Json.Linq.JObject.Parse(responseString);

                string messageContent = responseJson["choices"][0]["message"]["content"].ToString();

                string cronExpression = ExtractCronFromContent(messageContent);

                if (string.IsNullOrEmpty(cronExpression))
                {
                    string reasoningContent = ExtractReasoning(messageContent);
                    cronExpression = ExtractCronFromContent(reasoningContent);
                }

                return cronExpression;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing response: {ex.Message}");
                return string.Empty;
            }
        }

        private string ExtractCronFromContent(string content)
        {
            var cronPattern = @"\d+\s\d+\s\d+\s[?*\d]+\s[?*\d]+\s[?*\d]+";

            var match = System.Text.RegularExpressions.Regex.Match(content, cronPattern);

            if (match.Success)
            {
                return match.Value.Trim();
            }

            return string.Empty;
        }

        private string ExtractReasoning(string content)
        {
            var startReasoningIndex = content.IndexOf("<reasoning>");
            var endReasoningIndex = content.IndexOf("</reasoning>");

            if (startReasoningIndex >= 0 && endReasoningIndex > startReasoningIndex)
            {
                return content.Substring(startReasoningIndex + "<reasoning>".Length, endReasoningIndex - startReasoningIndex - "<reasoning>".Length);
            }

            return string.Empty;
        }
    }

    public class Message
    {
        public string role { get; set; }
        public string content { get; set; }
    }

    public class CronRequest
    {
        public List<Message> messages { get; set; }
    }
}
