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

namespace InvoiceLlm
{
    public class Reconcile
    {
        private readonly ILogger<Reconcile> _logger;
        private static BlobContainerClient processedContainerClient = Settings.ProcessedContainerClient;
        private static BlobContainerClient sourceContainerClient = Settings.SourceContainerClient;

        public Reconcile(ILogger<Reconcile> logger)
        {
            _logger = logger;
        }

        public record ReconcileMetadata(bool isLocal, string loanNumber);

        [Function("Reconcile")]
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
                string jsonExcel = string.Concat(Directory.GetParent(Environment.CurrentDirectory)?.Parent?.Parent?.FullName, Settings.LocalSourceDirectory, loanNumber, "\\", loanNumber, ".json");
                string jsonFr = string.Concat(Directory.GetParent(Environment.CurrentDirectory)?.Parent?.Parent?.FullName, Settings.LocalSourceDirectory, loanNumber, "\\", loanNumber, "_FrOut.json");
                string jsonArray1 = File.ReadAllText(jsonExcel);
                string jsonArray2 = File.ReadAllText(jsonFr);
                List<string> keyFields = new List<string> { "Service Date", "Invoice Number" };  
                Tuple<string, string> reconcileData = CompareJsonArrays(jsonArray1, jsonArray2, keyFields); 
                string matchingJson = reconcileData.Item1;
                string nonMatchingJson = reconcileData.Item2;
                
                // Output the JSON or do something with it...  
                string jsonFrMatching = string.Concat(Directory.GetParent(Environment.CurrentDirectory)?.Parent?.Parent?.FullName, Settings.LocalSourceDirectory, loanNumber, "\\", loanNumber, "_FuzzyMatching.json");
                _logger.LogInformation($"Saving Matching JSON to file '{jsonFrMatching}'");
                File.WriteAllText(jsonFrMatching, matchingJson);
                        
                string jsonFrNonMatching = string.Concat(Directory.GetParent(Environment.CurrentDirectory)?.Parent?.Parent?.FullName, Settings.LocalSourceDirectory, loanNumber, "\\", loanNumber, "_FuzzyNonMatching.json");
                _logger.LogInformation($"Saving Non Matching Rows JSON to file '{jsonFrNonMatching}'");
                File.WriteAllText(jsonFrNonMatching, nonMatchingJson);

            }
            else
            {
                return await ProcessRemoteReconcile(loanNumber);
            }
            return true;
        }

        public static Tuple<string, string> CompareJsonArrays(string jsonArray1, string jsonArray2, List<string> keyFields)  
        {  
            var array1 = JArray.Parse(jsonArray1);  
            var array2 = JArray.Parse(jsonArray2);  
    
            var matchingRows = new JArray();  
            var nonMatchingRows = new JArray();  
    
            // Create a dictionary to hold the keys from array2 for faster lookup  
            var array2Dictionary = new Dictionary<string, JObject>();  
            foreach (var item in array2)  
            {  
                var key = string.Join("_", keyFields.Select(k => (string)item[k]));  
                array2Dictionary[key] = (JObject)item;  
            }  
    
            foreach (var item1 in array1)  
            {  
                var key1 = string.Join("_", keyFields.Select(k => (string)item1[k]));  
                if (array2Dictionary.TryGetValue(key1, out JObject matchingItem2))  
                {  
                    matchingRows.Add(item1);  
                    // Remove the item from dictionary to prevent duplicates in the non-matching list  
                    array2Dictionary.Remove(key1);  
                }  
                else  
                {  
                    nonMatchingRows.Add(item1);  
                }  
            }  
    
            // Add remaining items from array2 to non-matching rows  
            foreach (var item2 in array2Dictionary.Values)  
            {  
                nonMatchingRows.Add(item2);  
            }  
    
            // Convert the results back to JSON arrays if needed  
            string matchingJson = matchingRows.ToString(Newtonsoft.Json.Formatting.Indented);  
            string nonMatchingJson = nonMatchingRows.ToString(Newtonsoft.Json.Formatting.Indented);

            return new Tuple<string, string>(matchingJson, nonMatchingJson);
    
        }  
    }
}
