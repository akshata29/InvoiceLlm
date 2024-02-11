using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Azure.Storage.Blobs;
using InvoiceLlm.Helper;
using FromBodyAttribute = Microsoft.Azure.Functions.Worker.Http.FromBodyAttribute;
using Polly;
using Azure;
using System.Net;
using System.Text.Json;
using System.Text;
using Newtonsoft.Json;  
using NPOI.SS.UserModel;  
using NPOI.XSSF.UserModel;
using NPOI.SS.Formula.Functions;
using Newtonsoft.Json.Linq;
using System;  
using System.Collections.Generic;  
using System.Linq;
using Azure.AI.OpenAI;
using Regex = System.Text.RegularExpressions.Regex;

namespace InvoiceLlm
{
    public class ReconcileLlm
    {
        private readonly ILogger<ReconcileLlm> _logger;
        private static BlobContainerClient processedContainerClient = Settings.ProcessedContainerClient;
        private static BlobContainerClient sourceContainerClient = Settings.SourceContainerClient;

        public ReconcileLlm(ILogger<ReconcileLlm> logger)
        {
            _logger = logger;
        }

        public record ReconcileMetadata(bool isLocal, string loanNumber);

        [Function("ReconcileLlm")]
        public async Task Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req, 
        [FromBody] ReconcileMetadata reconcileMetadata)
        {
            _logger.LogInformation("C# HTTP trigger function processed a request.");
            try
            {
                bool success = await ProcessReconcile(reconcileMetadata);
                if (!success)
                {
                    throw new Exception("Failed to process message");
                }
            }
            catch (Exception exe)
            {
                _logger.LogError(exe.ToString());
                throw;

            }
            //return new OkObjectResult("Welcome to Azure Functions!");
            return;
        }

        public Uri GetSourceFileUrl(string sourceFile)
        {
            var sourceBlob = Settings.SourceContainerClient.GetBlobClient(sourceFile);
            return sourceBlob.Uri;
        }

        private async Task<bool> ProcessReconcile(ReconcileMetadata reconcileMetadata)
        {
            var isLocal = reconcileMetadata.isLocal;
            var loanNumber = reconcileMetadata.loanNumber;

            if (isLocal)
            {
                List<string> keyFields = new List<string> { "Service Date", "Invoice Number" };
                string jsonExcel = string.Concat(Directory.GetParent(Environment.CurrentDirectory)?.Parent?.Parent?.FullName, Settings.LocalSourceDirectory, loanNumber, "\\", loanNumber, ".json");
                string jsonFr = string.Concat(Directory.GetParent(Environment.CurrentDirectory)?.Parent?.Parent?.FullName, Settings.LocalSourceDirectory, loanNumber, "\\", loanNumber, "_FrOut.json");
                string array1 = File.ReadAllText(jsonExcel);
                string array2 = File.ReadAllText(jsonFr);
                var jsonArray1 = JArray.Parse(array1);
                var jsonArray2 = JArray.Parse(array2);

                int chunks = (jsonArray1.Count() - 1) / 5 + 1;
                // for (int i = 0; i < chunks; i++)  
                // {  
                //     _logger.LogInformation($"Processing chunk {i + 1} of {chunks}");
                //     var chunkedJsonArray1 = jsonArray1.Skip(i * 5).Take(5).ToList();
                //     string content = $@"Array A:{JsonConvert.SerializeObject(chunkedJsonArray1, Formatting.Indented)}  
                //         Array B:{JsonConvert.SerializeObject(jsonArray2, Formatting.Indented)}  
                //         ";
                //     var reconcileData = await PerformReconcile(content, keyFields);
                    
                //     string jsonFrMatching = string.Concat(Directory.GetParent(Environment.CurrentDirectory)?.Parent?.Parent?.FullName, Settings.LocalSourceDirectory, loanNumber, "\\", loanNumber, "_LlmMatching", i.ToString(), ".json");
                //     reconcileData = Regex.Replace(reconcileData, "```json", "");  
                //     reconcileData = Regex.Replace(reconcileData, "```", "");  
                //     _logger.LogInformation($"Saving Matching JSON to file '{jsonFrMatching}'");
                //     File.WriteAllText(jsonFrMatching, reconcileData);
                // }

                // Create a list to hold the merged JSON data  
                JArray mergedJsonArray = new JArray();
                      
                // Combine all Matching Results
                for (int i = 0; i < chunks; i++)  
                {  
                    string filePath = string.Concat(Directory.GetParent(Environment.CurrentDirectory)?.Parent?.Parent?.FullName, Settings.LocalSourceDirectory, loanNumber, "\\", loanNumber, "_LlmMatching", i.ToString(), ".json");
                    if (File.Exists(filePath))  
                    {  
                        string jsonData = File.ReadAllText(filePath);  
                        JArray jsonArray = JArray.Parse(jsonData);
                        mergedJsonArray.Merge(jsonArray);  
                    }  
                }

                string normalizedJson = Newtonsoft.Json.JsonConvert.SerializeObject(mergedJsonArray, 
                    new Newtonsoft.Json.JsonSerializerSettings { Formatting = Newtonsoft.Json.Formatting.Indented });
                string llmMatchingFilePath = string.Concat(Directory.GetParent(Environment.CurrentDirectory)?.Parent?.Parent?.FullName, Settings.LocalSourceDirectory, loanNumber, "\\", loanNumber, "_LlmMatching", ".json");
                File.WriteAllText(llmMatchingFilePath, normalizedJson);
            }
            return true;
        }

        private async Task<string> PerformReconcile(string content, List<string> keyFields)  
        {  
            OpenAIClient client = new OpenAIClient(
                    new Uri(Settings.OpenAiEndpoint),
                    new AzureKeyCredential(Settings.OpenAiKey));

            _logger.LogInformation("Performing Reconcile using Llm");
            var chatCompletionsOptions = new ChatCompletionsOptions()
            {
                DeploymentName = Settings.OpenAiModel,
                Messages =
                {
                    // The system message represents instructions or other guidance about how the assistant should behave
                    new ChatRequestSystemMessage(Settings.OpenAiSystemMessageMatch),
                    // User messages represent current or historical input from the end user
                    new ChatRequestUserMessage(content),
                },
                MaxTokens = 4096,
                Temperature = 0,
            };

            Response<ChatCompletions> response = await client.GetChatCompletionsAsync(chatCompletionsOptions);
            ChatResponseMessage responseMessage = response.Value.Choices[0].Message;

            _logger.LogInformation("Reconcile completed by Llm");

            var matchingJson = responseMessage.Content; 
            return matchingJson;
    
        }  
    }
}
