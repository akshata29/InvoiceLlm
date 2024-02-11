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

namespace InvoiceLlm
{
    public class AnalyzeInvoice
    {
        private readonly ILogger<AnalyzeInvoice> _logger;
        private static List<DocumentAnalysisClient> formRecognizerClients = Settings.FormRecognizerClients;
        private static BlobContainerClient processedContainerClient = Settings.ProcessedContainerClient;
        private static BlobContainerClient sourceContainerClient = Settings.SourceContainerClient;

        public AnalyzeInvoice(ILogger<AnalyzeInvoice> logger)
        {
            _logger = logger;
        }

        public record InvoiceMetadata(bool isLocal, string loanNumber, string excelSource, string corpAdvSource, string invSource);

        [Function("AnalyzeInvoice")]
        public async Task Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req, 
        [FromBody] InvoiceMetadata invoiceMetadata)
        {
            _logger.LogInformation("C# HTTP trigger function processed a request.");
            try
            {
                bool success = await ProcessInvoices(invoiceMetadata);
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

        private DocumentAnalysisClient GetFormRecognizerClient(int index)
        {
            try
            {
                int clientCount = formRecognizerClients.Count;
                if (index < clientCount)
                {
                    return formRecognizerClients[index];
                }
                else
                {
                    int mod = index % clientCount;
                    if (mod < clientCount)
                    {
                        return formRecognizerClients[mod];
                    }
                    else
                    {
                        return GetFormRecognizerClient(index - 1);
                    }
                }
            }
            catch
            {
                return formRecognizerClients.First();
            }
        }

        private async Task<bool> ProcessInvoices(InvoiceMetadata invoiceMetadata)
        {
            var analyzeOutput = await AnalyzeInvoiceRecognition(invoiceMetadata.isLocal, invoiceMetadata.loanNumber, invoiceMetadata.excelSource, 
            invoiceMetadata.corpAdvSource, invoiceMetadata.invSource);
            if (!analyzeOutput)
            {
                _logger.LogError($"Failed to get Form Recognizer output for loan '{invoiceMetadata.loanNumber}'. Stopping processing and abandoning message.");
                return false;
            }
            var processedRecognition = await ProcessFormRecognition(invoiceMetadata.isLocal, invoiceMetadata.loanNumber, invoiceMetadata.excelSource, 
            invoiceMetadata.corpAdvSource, invoiceMetadata.invSource);
            if (!processedRecognition)
            {
                _logger.LogError($"Unable to process the output data for loan '{invoiceMetadata.loanNumber}'. Stopping processing and abandoning message.");
                return false;
            }
            return true;
        }

        private static object GetCellValue(ICell cell)  
        {  
            if (cell == null)  
            {  
                return null;  
            }  
    
            switch (cell.CellType)  
            {  
                case CellType.Numeric:  
                    if (DateUtil.IsCellDateFormatted(cell))  
                    {  
                        return cell.DateCellValue.ToString("yyyy-MM-dd");
                    }  
                    else  
                    {  
                        return cell.NumericCellValue; 
                    }
                case CellType.String:  
                    return cell.StringCellValue;  
                case CellType.Boolean:  
                    return cell.BooleanCellValue;  
                case CellType.Formula:  
                    return cell.CellFormula;  
                default:  
                    return null;  
            }  
        }  
        private static string ExcelToJsonConverter(string filePath, string worksheetName)
        {
            string json = string.Empty;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                IWorkbook workbook = new XSSFWorkbook(fs);
                ISheet worksheet = workbook.GetSheet(worksheetName);
                IRow headerRow = worksheet.GetRow(0);
                List<string> columnNames = new List<string>();  
                for (int col = 0; col < headerRow.LastCellNum; col++)  
                {  
                    columnNames.Add(headerRow.GetCell(col).StringCellValue);  
                }
                List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
                for (int row = 1; row <= worksheet.LastRowNum; row++)  
                {  
                    IRow dataRow = worksheet.GetRow(row);  
                    Dictionary<string, object> rowData = new Dictionary<string, object>();  
                    for (int col = 0; col < dataRow.LastCellNum; col++)  
                    {  
                        rowData[columnNames[col]] = GetCellValue(dataRow.GetCell(col));  
                    }  
                    rows.Add(rowData);  
                }  
                // for (int i = worksheet.FirstRowNum + 1; i <= worksheet.LastRowNum; i++)
                // {
                //     IRow row = worksheet.GetRow(i);
                //     Dictionary<string, object> dict = new Dictionary<string, object>();
                //     for (int j = 0; j < row.LastCellNum; j++)
                //     {
                //         dict.Add(headerRow.GetCell(j).StringCellValue, row.GetCell(j).StringCellValue);
                //     }
                //     rows.Add(dict);
                // }
                json = JsonConvert.SerializeObject(rows, Formatting.Indented);
            }
            return json;
        }
        private async Task<bool> AnalyzeInvoiceRecognition(bool isLocal, string loanNumber, string excelSource, string corpAdvSource, string invSource)
        {
            _logger.LogInformation($"Processing loan '{loanNumber}'");
            _logger.LogInformation($"Processing excel source '{excelSource}'");
            _logger.LogInformation($"Processing corporate advance source '{corpAdvSource}'");
            _logger.LogInformation($"Processing invoice source '{invSource}'");
            if (isLocal)
            {
                // Read the excel source file from local directory
                string excelFilePath = string.Concat(Directory.GetParent(Environment.CurrentDirectory)?.Parent?.Parent?.FullName, Settings.LocalSourceDirectory, loanNumber, "\\", excelSource);
                _logger.LogInformation($"Processing excel file '{excelFilePath}'");

                // Convert the excel file to JSON
                string excelJson = ExcelToJsonConverter(excelFilePath, "Advances");

                // Save the JSON to a local file
                string jsonFilePath = string.Concat(Directory.GetParent(Environment.CurrentDirectory)?.Parent?.Parent?.FullName, Settings.LocalSourceDirectory, loanNumber, "\\", loanNumber, ".json");
                _logger.LogInformation($"Saving JSON to file '{jsonFilePath}'");
                File.WriteAllText(jsonFilePath, excelJson);

                // Read the corporate advance source file(s) from local directory
                string corpAdvFilePath = string.Concat(Directory.GetParent(Environment.CurrentDirectory)?.Parent?.Parent?.FullName, Settings.LocalSourceDirectory, loanNumber, "\\", corpAdvSource);
                // Read all the corporate advance source files from the directory
                string[] corpAdvFiles = Directory.GetFiles(corpAdvFilePath);
                corpAdvFiles = corpAdvFiles.Where(f => Path.GetExtension(f) == ".pdf").ToArray();
                foreach (string corpAdvFile in corpAdvFiles)
                {
                    // Check if we already have processed the file and corresponding JSON exists
                    string existingJson = string.Concat(Directory.GetParent(Environment.CurrentDirectory)?.Parent?.Parent?.FullName, Settings.LocalSourceDirectory, loanNumber, "\\", corpAdvSource, "\\", Path.GetFileNameWithoutExtension(corpAdvFile), ".json");
                    if (File.Exists(existingJson))
                    {
                        _logger.LogInformation($"Skipping processing of corporate advance file '{corpAdvFile}' as JSON already exists");
                        continue;
                    }
                    _logger.LogInformation($"Processing corporate advance file '{corpAdvFile}'");
                    try
                    {
                        var formRecognizerClient = GetFormRecognizerClient(0);
                        using var stream = new FileStream(corpAdvFile, FileMode.Open);

                        AnalyzeDocumentOperation operation = await formRecognizerClient.AnalyzeDocumentAsync(WaitUntil.Completed, 
                            Settings.InvoiceProcessingModel, stream);
                        var response = await operation.WaitForCompletionAsync();
                        string frJson = response.GetRawResponse().Content.ToString();
                        AnalyzeResult result = operation.Value;
                        // Save the output to a file
                        string outputFilePath = string.Concat(Directory.GetParent(Environment.CurrentDirectory)?.Parent?.Parent?.FullName, Settings.LocalSourceDirectory, loanNumber, "\\", corpAdvSource, "\\", Path.GetFileNameWithoutExtension(corpAdvFile), ".json");
                        _logger.LogInformation($"Saving JSON to file '{outputFilePath}'");
                        File.WriteAllText(outputFilePath, frJson);
                    }
                    catch (Azure.RequestFailedException are)
                    {
                        if (are.ErrorCode == "InvalidRequest")
                        {
                            _logger.LogError($"Failed to process file at URL:{corpAdvFiles}. You may need to set permissions from the Form Recognizer to access your storage account. {are.ToString()}");
                        }
                        else
                        {
                            _logger.LogError($"Failed to process file at URL:{corpAdvFiles}. {are.ToString()}");
                        }
                        return false;
                    }
                    catch (Exception exe)
                    {

                        _logger.LogError($"Failed to process file at URL:{corpAdvFiles}. {exe.ToString()}");
                        return false;
                    }

                }

                // Read the Invoice source file(s) from local directory
                string invFilePath = string.Concat(Directory.GetParent(Environment.CurrentDirectory)?.Parent?.Parent?.FullName, Settings.LocalSourceDirectory, loanNumber, "\\", invSource);
                // Read all the corporate advance source files from the directory
                string[] invFiles = Directory.GetFiles(invFilePath);
                invFiles = invFiles.Where(f => Path.GetExtension(f) == ".pdf").ToArray();
                foreach (string invFile in invFiles)
                {
                    // Check if we already have processed the file and corresponding JSON exists
                    string existingJson = string.Concat(Directory.GetParent(Environment.CurrentDirectory)?.Parent?.Parent?.FullName, Settings.LocalSourceDirectory, loanNumber, "\\", invSource, "\\", Path.GetFileNameWithoutExtension(invFile), ".json");
                    if (File.Exists(existingJson))
                    {
                        _logger.LogInformation($"Skipping processing of Invoice file '{invFile}' as JSON already exists");
                        continue;
                    }
                    _logger.LogInformation($"Processing Invoice file '{invFile}'");
                    try
                    {
                        var formRecognizerClient = GetFormRecognizerClient(0);
                        using var stream = new FileStream(invFile, FileMode.Open);

                        AnalyzeDocumentOperation operation = await formRecognizerClient.AnalyzeDocumentAsync(WaitUntil.Completed, 
                            Settings.InvoiceProcessingModel, stream);
                        AnalyzeResult result = operation.Value;
                        var response = await operation.WaitForCompletionAsync();
                        string frJson = response.GetRawResponse().Content.ToString();

                        _logger.LogInformation($"Completed Analyze '{invFile}'");

                        // Save the output to a file
                        string outputFilePath = string.Concat(Directory.GetParent(Environment.CurrentDirectory)?.Parent?.Parent?.FullName, Settings.LocalSourceDirectory, loanNumber, "\\", invSource, "\\", Path.GetFileNameWithoutExtension(invFile), ".json");
                        _logger.LogInformation($"Saving JSON to file '{outputFilePath}'");
                        File.WriteAllText(outputFilePath, frJson);
                    }
                    catch (Azure.RequestFailedException are)
                    {
                        if (are.ErrorCode == "InvalidRequest")
                        {
                            _logger.LogError($"Failed to process file at URL:{invFile}. You may need to set permissions from the Form Recognizer to access your storage account. {are.ToString()}");
                        }
                        else
                        {
                            _logger.LogError($"Failed to process file at URL:{invFile}. {are.ToString()}");
                        }
                        return false;
                    }
                    catch (Exception exe)
                    {

                        _logger.LogError($"Failed to process file at URL:{invFile}. {exe.ToString()}");
                        return false;
                    }

                }

                return true;
            }
            else
            {
                // Random jitterier = new();
                // CancellationTokenSource source = new CancellationTokenSource();
                // try
                // {
                //     var formRecognizerClient = GetFormRecognizerClient(0);

                //     //Retry policy to back off if too many calls are made to the Form Recognizer
                //     var retryPolicy = Policy.Handle<RequestFailedException>(e => e.Status == (int)HttpStatusCode.TooManyRequests)
                //         .WaitAndRetryAsync(5, retryAttempt => TimeSpan.FromSeconds(retryAttempt++) + TimeSpan.FromMilliseconds(jitterier.Next(0, 1000)));

                //     AnalyzeDocumentOperation operation = null;

                //     var pollyResult = await retryPolicy.ExecuteAndCaptureAsync(async token =>
                //     {
                //         operation = await formRecognizerClient.AnalyzeDocumentFromUriAsync(WaitUntil.Started, Settings.InvoiceProcessingModel, fileUri);
                //     }, source.Token);


                //     if (pollyResult.Outcome == OutcomeType.Failure)
                //     {
                //         _logger.LogError($"Policy retries failed for {fileUri}. Resulting exception: {pollyResult.FinalException}");
                //         return false;
                //     }

                //     //Using this sleep vs. operation.WaitForCompletion() to avoid over loading the endpoint
                //     do
                //     {
                //         System.Threading.Thread.Sleep(2000);
                //         await retryPolicy.ExecuteAndCaptureAsync(async token =>
                //         {
                //             await operation.UpdateStatusAsync();
                //         }, source.Token);

                //         if (pollyResult.Outcome == OutcomeType.Failure)
                //         {
                //             _logger.LogError($"Policy retries failed for calling UpdateStatusAsync on {fileUri}. Resulting exception: {pollyResult.FinalException}");
                //         }

                //     } while (!operation.HasCompleted);


                //     string output = JsonSerializer.Serialize(operation.Value, new JsonSerializerOptions() { WriteIndented = true });
                //     // Save the output to a blob
                //     var blobClient = processedContainerClient.GetBlobClient($"{loanNumber}.json");
                //     using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(output)))
                //     {
                //         await blobClient.UploadAsync(stream, true);
                //     }

                //     return true;
                // }
                // catch (Azure.RequestFailedException are)
                // {
                //     if (are.ErrorCode == "InvalidRequest")
                //     {
                //         _logger.LogError($"Failed to process file at URL:{fileUri.AbsoluteUri}. You may need to set permissions from the Form Recognizer to access your storage account. {are.ToString()}");
                //     }
                //     else
                //     {
                //         _logger.LogError($"Failed to process file at URL:{fileUri.AbsoluteUri}. {are.ToString()}");
                //     }
                //     return false;
                // }
                // catch (Exception exe)
                // {

                //     _logger.LogError($"Failed to process file at URL:{fileUri.AbsoluteUri}. {exe.ToString()}");
                //     return false;
                // }
                return true;
            }
        }
        private static string ReplaceJsonPlaceHolder(string json, Dictionary<string, object> values)  
        {  
            // Replaces all placeholders with values  
            foreach (var item in values)  
            {  
                string placeholder = "<" + item.Key + ">";  
                json = json.Replace(placeholder, item.Value.ToString());  
            }
            return json;  
        }  
        private async Task<bool> ProcessFormRecognition(bool isLocal, string loanNumber, string excelSource, string corpAdvSource, string invSource)
        {
            if (isLocal)
            {
                List<string> extractedData = new List<string>();  
                string templateStructure = "{\"Payment Date\":\"<Payment_Date>\",\"Service Date\":\"<Service_Date>\", \"Expense Type\": \"\",\"Additional Expense Comments\":\"<Additional_Expense_Comments>\",\"Expense Description\": \"<Expense_Description>\",\"Amount Paid\": \"<Amount_Paid>\",\"Amount Claimed\": \"\",\"Amount Not Claimed\": \"\",\"Unclaimed Amount Reason\": \"\",\"Vendor Name\": \"<Vendor_Name>\",\"Invoice Number\": \"<Invoice_Number>\",\"Fee Type Code\": \"\",\"Recovery Type\": \"\",\"Actual Recovery Code\": \"\",\"Expense Code\": \"\",\"Fee Reference Comments\": \"\",\"File Name\": \"\",\"Document Available\": \"\",\"Notes\": \"<Notes>\"}";
                // Read the corporate advance source file(s) from local directory
                string corpAdvFilePath = string.Concat(Directory.GetParent(Environment.CurrentDirectory)?.Parent?.Parent?.FullName, Settings.LocalSourceDirectory, loanNumber, "\\", corpAdvSource);
                // Read all the corporate advance source files from the directory
                string[] corpAdvFiles = Directory.GetFiles(corpAdvFilePath);
                // Filter the corporate advance files to get only .json files
                corpAdvFiles = corpAdvFiles.Where(f => Path.GetExtension(f) == ".json").ToArray();
                foreach (string corpAdvFile in corpAdvFiles)
                {
                    _logger.LogInformation($"Processing corporate advance JSON data '{corpAdvFile}'");
                    try
                    {
                        string json = File.ReadAllText(corpAdvFile);
                        JObject invoices = JsonConvert.DeserializeObject<JObject>(json);  
                        var analyzeResults = invoices["analyzeResult"];
                
                        string paymentDate = string.Empty;  
                        string serviceDate = string.Empty;  
                        string invoiceNbr = string.Empty;  
                        string vendorName = string.Empty;  
                        string expenseDesc = string.Empty;  
                        string additionalExpenseComments = string.Empty;  
                        string amountPaid = string.Empty;  
                        string notes = string.Empty;  
                
                        foreach (var invoice in analyzeResults["documents"])  
                        {  
                            foreach (var field in invoice["fields"])  
                            {  
                                string name = field.Path.Split('.').Last(); // Get the field name  
                                var content = field.First["content"];  
                
                                if (name != "Items")  
                                {  
                                    if (name == "VendorName")  
                                    {  
                                        vendorName = content.ToString();  
                                    }  
                                    if (name == "InvoiceId")  
                                    {  
                                        invoiceNbr = content.ToString();  
                                    }  
                                    if (name == "CustomerAddress")  
                                    {  
                                        notes = content.ToString();  
                                    }  
                                }  
                            }  
                            var items = invoice["fields"]["Items"]["valueArray"];
                            if (items != null)  
                            {  
                                int idx = 0;
                                foreach (JObject item in items)  
                                {  
                                    JObject amountObject = (JObject)item["valueObject"]?["Amount"];
                                    JObject dateObject = (JObject)item["valueObject"]?["Date"];
                                    JObject descObject = (JObject)item["valueObject"]?["Description"];

                                    if (amountObject != null)
                                    {
                                        if ((string)amountObject["type"] == "currency")
                                        {
                                            amountPaid = (string)amountObject["valueCurrency"]?["amount"];
                                        }
                                        else
                                        {
                                            amountPaid = (string)amountObject["content"];
                                        }
                                    }

                                    if (dateObject != null)
                                    {
                                        if ((string)dateObject["type"] == "date")
                                        {
                                            serviceDate = (string)dateObject["valueDate"];
                                        }
                                        else
                                        {
                                            serviceDate = (string)dateObject["content"];
                                        }
                                    }

                                    if (descObject != null)
                                    {
                                        expenseDesc = (string)descObject["content"];
                                    }                        
                                }
                            }
                        }

                        // foreach (var kv in analyzeResults["key_value_pairs"])  
                        // {  
                        //     if (kv["value"] != null && kv["value"].HasValues)  
                        //     {  
                        //         var keyContent = (string)kv["key"]["content"];  
                        //         var valueContent = (string)kv["value"]["content"];  
                        
                        //         if (!string.IsNullOrEmpty(keyContent) && !string.IsNullOrEmpty(valueContent))  
                        //         {  
                        //             if (keyContent == "PaymentDate")  
                        //             {  
                        //                 paymentDate = valueContent;  
                        //             }  
                        //         }  
                        //     }  
                        // }  
                
                        // Remove the prefix "60" from the invoice number if needed  
                        if (!string.IsNullOrEmpty(invoiceNbr) && invoiceNbr.StartsWith("60"))  
                        {  
                            invoiceNbr = invoiceNbr.Substring(2);  
                        }  
                
                        Dictionary<string, object> values = new Dictionary<string, object>
                        {  
                            ["Service_Date"] = serviceDate,  
                            ["Amount_Paid"] = amountPaid,  
                            ["Vendor_Name"] = vendorName,  
                            ["Invoice_Number"] = invoiceNbr,  
                            ["Expense_Description"] = expenseDesc,  
                            ["Additional_Expense_Comments"] = additionalExpenseComments,  
                            ["Notes"] = notes  
                        };  
                        string filledItem = ReplaceJsonPlaceHolder(templateStructure, values);
                        extractedData.Add(filledItem);
                    }
                    catch (Azure.RequestFailedException are)
                    {
                        if (are.ErrorCode == "InvalidRequest")
                        {
                            _logger.LogError($"Failed to process file at URL:{corpAdvFiles}. You may need to set permissions from the Form Recognizer to access your storage account. {are.ToString()}");
                        }
                        else
                        {
                            _logger.LogError($"Failed to process file at URL:{corpAdvFiles}. {are.ToString()}");
                        }
                        return false;
                    }
                    catch (Exception exe)
                    {

                        _logger.LogError($"Failed to process file at URL:{corpAdvFiles}. {exe.ToString()}");
                        return false;
                    }
                }

                // Process Invoice Files Now
                string invFilePath = string.Concat(Directory.GetParent(Environment.CurrentDirectory)?.Parent?.Parent?.FullName, Settings.LocalSourceDirectory, loanNumber, "\\", invSource);
                // Read all the corporate advance source files from the directory
                string[] invFiles = Directory.GetFiles(invFilePath);
                // Filter the corporate advance files to get only .json files
                invFiles = invFiles.Where(f => Path.GetExtension(f) == ".json").ToArray();
                foreach (string invFile in invFiles)
                {
                    _logger.LogInformation($"Processing Invoice JSON data '{invFile}'");
                    try
                    {
                        string invJson = File.ReadAllText(invFile);
                        JObject invoices = JsonConvert.DeserializeObject<JObject>(invJson);  
                        var analyzeResults = invoices["analyzeResult"];

                        string paymentDate = string.Empty;  
                        string serviceDate = string.Empty;  
                        string invoiceNbr = string.Empty;  
                        string vendorName = string.Empty;  
                        string expenseDesc = string.Empty;  
                        string additionalExpenseComments = string.Empty;  
                        string amountPaid = string.Empty;  
                        string notes = string.Empty;  
                
                        foreach (var invoice in analyzeResults["documents"])  
                        {  
                            foreach (var field in invoice["fields"])  
                            {  
                                string name = field.Path.Split('.').Last(); // Get the field name  
                                var content = field.First["content"];  
                
                                if (name != "Items")  
                                {  
                                    if (name == "VendorName")  
                                    {  
                                        vendorName = content.ToString();  
                                    }  
                                    if (name == "InvoiceId")  
                                    {  
                                        invoiceNbr = content.ToString();  
                                    }  
                                    if (name == "CustomerAddress")  
                                    {  
                                        notes = content.ToString();  
                                    }  
                                }  
                            }  
                            var items = invoice["fields"]["Items"]["valueArray"];
                            if (items != null)  
                            {  
                                int idx = 0;
                                foreach (JObject item in items)  
                                {  
                                    JObject amountObject = (JObject)item["valueObject"]?["Amount"];
                                    JObject dateObject = (JObject)item["valueObject"]?["Date"];
                                    JObject descObject = (JObject)item["valueObject"]?["Description"];

                                    if (amountObject != null)
                                    {
                                        if ((string)amountObject["type"] == "currency")
                                        {
                                            amountPaid = (string)amountObject["valueCurrency"]?["amount"];
                                        }
                                        else
                                        {
                                            amountPaid = (string)amountObject["content"];
                                        }
                                    }

                                    if (dateObject != null)
                                    {
                                        if ((string)dateObject["type"] == "date")
                                        {
                                            serviceDate = (string)dateObject["valueDate"];
                                        }
                                        else
                                        {
                                            serviceDate = (string)dateObject["content"];
                                        }
                                    }

                                    if (descObject != null)
                                    {
                                        expenseDesc = (string)descObject["content"];
                                    }
                        
                                }
                            }
                        }  
                
                        // foreach (var kv in analyzeResults["key_value_pairs"])  
                        // {  
                        //     if (kv["value"] != null && kv["value"].HasValues)  
                        //     {  
                        //         var keyContent = (string)kv["key"]["content"];  
                        //         var valueContent = (string)kv["value"]["content"];  
                        
                        //         if (!string.IsNullOrEmpty(keyContent) && !string.IsNullOrEmpty(valueContent))  
                        //         {  
                        //             if (keyContent == "PaymentDate")  
                        //             {  
                        //                 paymentDate = valueContent;  
                        //             }  
                        //         }  
                        //     }  
                        // }  
                
                        // Remove the prefix "60" from the invoice number if needed  
                        if (!string.IsNullOrEmpty(invoiceNbr) && invoiceNbr.StartsWith("60"))  
                        {  
                            invoiceNbr = invoiceNbr.Substring(2);  
                        }  
                
                        Dictionary<string, object> values = new Dictionary<string, object>
                        {  
                            ["Service_Date"] = serviceDate,  
                            ["Amount_Paid"] = amountPaid,  
                            ["Vendor_Name"] = vendorName,  
                            ["Invoice_Number"] = invoiceNbr,  
                            ["Expense_Description"] = expenseDesc,  
                            ["Additional_Expense_Comments"] = additionalExpenseComments,  
                            ["Notes"] = notes  
                        };  
                        string filledItem = ReplaceJsonPlaceHolder(templateStructure, values);
                        extractedData.Add(filledItem);
                    }
                    catch (Azure.RequestFailedException are)
                    {
                        if (are.ErrorCode == "InvalidRequest")
                        {
                            _logger.LogError($"Failed to process file at URL:{corpAdvFiles}. You may need to set permissions from the Form Recognizer to access your storage account. {are.ToString()}");
                        }
                        else
                        {
                            _logger.LogError($"Failed to process file at URL:{corpAdvFiles}. {are.ToString()}");
                        }
                        return false;
                    }
                    catch (Exception exe)
                    {

                        _logger.LogError($"Failed to process file at URL:{corpAdvFiles}. {exe.ToString()}");
                        return false;
                    }
                }

                // var output = new Dictionary<string, object>  
                // {  
                //     {"Advances", extractedData}  
                // }; 
                string frJsonOut = Newtonsoft.Json.JsonConvert.SerializeObject(extractedData, new Newtonsoft.Json.JsonSerializerSettings { Formatting = Newtonsoft.Json.Formatting.Indented });
                frJsonOut = frJsonOut.Replace("\\", "");
                frJsonOut = frJsonOut.Replace("\"{", "{");
                frJsonOut = frJsonOut.Replace("}\"", "}");
                // Save the output to a file
                string outputFilePath = string.Concat(Directory.GetParent(Environment.CurrentDirectory)?.Parent?.Parent?.FullName, Settings.LocalSourceDirectory, loanNumber, "\\", loanNumber, "_FrOut.json");
                _logger.LogInformation($"Saving JSON to file '{outputFilePath}'");
                File.WriteAllText(outputFilePath, frJsonOut);
            }
            return true;
        }
    }
}
