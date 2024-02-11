using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using System.Collections.Generic;
using Azure;

namespace InvoiceLlm.Helper
{
    public class Settings
    {
        static Settings()
        {
            var loggerFactory = new LoggerFactory();
            storageLogger = loggerFactory.CreateLogger<StorageHelper>();
        }
        private static ILogger<StorageHelper> storageLogger;
        private static string _endpoint = string.Empty;
        private static List<string> _keys = new List<string>();
        private static string _openAiEndPoint = string.Empty;
        private static string _openAiKey = string.Empty;
        private static string _openAiModel = string.Empty;
        private static string _openAiSystemMessageMatch = string.Empty;
        private static string _sourceContainerName = string.Empty;
        private static string _processedContainerName = string.Empty;
        private static string _outputContainerName = string.Empty;
        private static string _storageAccountName = string.Empty;
        private static string _storageConnectionString = string.Empty;
        private static string _invoiceProcessingModel = string.Empty;
        private static string _localSourceDirectory = string.Empty;
        private static BlobContainerClient _sourceContainerClient;
        private static BlobContainerClient _processedContainerClient;
        private static BlobContainerClient _outputContainerClient;
        private static List<DocumentAnalysisClient> _formRecognizerClients = new List<DocumentAnalysisClient>();

        public static string Endpoint
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_endpoint))
                {
                    _endpoint = Environment.GetEnvironmentVariable("FormRecognizerEndpoint");
                }
                return _endpoint;
            }
        }
        public static List<string> Keys
        {
            get
            {
                if (_keys.Count == 0)
                {
                    var tmp = Environment.GetEnvironmentVariable("FormRecognizerKey");
                    if (!string.IsNullOrWhiteSpace(tmp))
                    {
                        _keys.AddRange(tmp.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    }
                    else
                    {
                        storageLogger.LogError("FormRecognizerKey is empty");
                    }
                }
                return _keys;
            }
        }
        public static string OpenAiEndpoint
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_openAiEndPoint))
                {
                    _openAiEndPoint = Environment.GetEnvironmentVariable("OpenAiEndPoint");
                }
                return _openAiEndPoint;
            }
        }
        public static string OpenAiSystemMessageMatch
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_openAiSystemMessageMatch))
                {
                    _openAiSystemMessageMatch = Environment.GetEnvironmentVariable("OpenAiSystemMessageMatch");
                }
                return _openAiSystemMessageMatch;
            }
        }
        public static string OpenAiKey
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_openAiKey))
                {
                    _openAiKey = Environment.GetEnvironmentVariable("OpenAiKey");
                }
                return _openAiKey;
            }
        }
        public static string OpenAiModel
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_openAiModel))
                {
                    _openAiModel = Environment.GetEnvironmentVariable("OpenAiModel");
                }
                return _openAiModel;
            }
        }
        public static string SourceContainerName
        {
            get
            {
                if (string.IsNullOrEmpty(_sourceContainerName))
                {
                    _sourceContainerName = Environment.GetEnvironmentVariable("SourceContainer");
                    if (string.IsNullOrEmpty(_sourceContainerName)) storageLogger.LogError("SourceContainer setting is empty!");
                }
                return _sourceContainerName;

            }
        }
        public static string ProcessedContainerName
        {
            get
            {
                if (string.IsNullOrEmpty(_processedContainerName))
                {
                    _processedContainerName = Environment.GetEnvironmentVariable("ProcessedContainer");
                    if(string.IsNullOrEmpty(_processedContainerName)) storageLogger.LogError("ProcessedContainer setting is empty!");
                }
                return _processedContainerName;

            }
        }
        public static string OutputContainerName
        {
            get
            {
                if (string.IsNullOrEmpty(_outputContainerName))
                {
                    _outputContainerName = Environment.GetEnvironmentVariable("OutputContainer");
                    if (string.IsNullOrEmpty(_outputContainerName)) storageLogger.LogError("OutputContainer setting is empty!");
                }
                return _outputContainerName;

            }
        }
        public static string StorageAccountName
        {
            get
            {
                if (string.IsNullOrEmpty(_storageAccountName))
                {
                    _storageAccountName = Environment.GetEnvironmentVariable("StorageAccountName");
                    if (string.IsNullOrEmpty(_storageAccountName)) storageLogger.LogError("StorageAccountName setting is empty!");
                }
                return _storageAccountName;

            }
        }
        public static string StorageConnectionString
        {
            get
            {
                if (string.IsNullOrEmpty(_storageConnectionString))
                {
                    _storageConnectionString = Environment.GetEnvironmentVariable("StorageConnectionString");
                    if (string.IsNullOrEmpty(_storageConnectionString)) storageLogger.LogError("StorageConnectionString setting is empty!");
                }
                return _storageConnectionString;

            }
        }
        public static string InvoiceProcessingModel
        {
            get
            {
                if (string.IsNullOrEmpty(_invoiceProcessingModel))
                {
                    _invoiceProcessingModel = Environment.GetEnvironmentVariable("InvoiceProcessingModel");
                    if (string.IsNullOrWhiteSpace(_invoiceProcessingModel)) _invoiceProcessingModel = "prebuilt-invoice";
                }
                return _invoiceProcessingModel;

            }
        }
        public static BlobContainerClient SourceContainerClient
        {
            get
            {
                if (_sourceContainerClient == null)
                {

                    //_sourceContainerClient = new StorageHelper(storageLogger).CreateBlobContainerClient(SourceContainerName, StorageAccountName);
                    _sourceContainerClient = new StorageHelper(storageLogger).CreateBlobContainerClientByConnectionString(StorageConnectionString, SourceContainerName);
                }
                return _sourceContainerClient;
            }
        }
        public static string LocalSourceDirectory
        {
            get
            {
                if (string.IsNullOrEmpty(_localSourceDirectory))
                {
                    _localSourceDirectory = Environment.GetEnvironmentVariable("LocalSourceDirectory");
                    if (string.IsNullOrWhiteSpace(_localSourceDirectory)) _localSourceDirectory = "..\\Data\\Loan\\";
                }
                return _localSourceDirectory;

            }
        }
        public static BlobContainerClient ProcessedContainerClient
        {
            get
            {
                if (_processedContainerClient == null)
                {
                    //_processedContainerClient = new StorageHelper(storageLogger).CreateBlobContainerClient(ProcessedContainerName, StorageAccountName);
                    _processedContainerClient = new StorageHelper(storageLogger).CreateBlobContainerClientByConnectionString(StorageConnectionString, SourceContainerName);
                }
                return _processedContainerClient;
            }
        }
        public static BlobContainerClient OutputContainerClient
        {
            get
            {
                if (_outputContainerClient == null)
                {
                    //_outputContainerClient = new StorageHelper(storageLogger).CreateBlobContainerClient(OutputContainerName, StorageAccountName);
                    _outputContainerClient = new StorageHelper(storageLogger).CreateBlobContainerClientByConnectionString(StorageConnectionString, SourceContainerName);
                }
                return _outputContainerClient;
            }
        }
       
        public static List<DocumentAnalysisClient> FormRecognizerClients
        {
            get
            {
                if(_formRecognizerClients.Count == 0)
                {
                    foreach(var key in Keys)
                    {
                        var  credential = new AzureKeyCredential(key);
                        var  formRecognizerClient = new DocumentAnalysisClient(new Uri(Settings.Endpoint), credential);
                        _formRecognizerClients.Add(formRecognizerClient);
                    }    
                }
                return _formRecognizerClients;
            }
        }
    }
}