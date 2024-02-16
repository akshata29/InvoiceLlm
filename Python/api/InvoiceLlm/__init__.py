import logging
import azure.functions as func
import os
import requests
import urllib.parse
from azure.storage.blob import generate_container_sas
import json
# Import Python libraries
import os
from openai import OpenAI, AzureOpenAI, AsyncAzureOpenAI
import json
import time
from requests import get, post
import re
import ast
import pandas as pd  
import json
from azure.storage.blob import BlobServiceClient, ContentSettings, generate_blob_sas
from decimal import Decimal

IncomingContainer = os.environ['IncomingContainer']
OutputContainer = os.environ['OutputContainer']
ProcessedContainer = os.environ['ProcessedContainer']
OpenAiDocStorName = os.environ['OpenAiDocStorName']
OpenAiDocStorKey = os.environ['OpenAiDocStorKey']
OpenAiDocConnStr = f"DefaultEndpointsProtocol=https;AccountName={OpenAiDocStorName};AccountKey={OpenAiDocStorKey};EndpointSuffix=core.windows.net"
FormRecognizerEndPoint = os.getenv('FormRecognizerEndPoint')
FormRecognizerKey = os.getenv('FormRecognizerKey')

def replaceJsonPlaceHolder(json, values):
  # replaces all placeholders with values
  for k, v in values.items():
      placeholder = "<%s>" % k
      json = json.replace(placeholder, str(v))

  return json

class DateEncoder(json.JSONEncoder):
    def default(self, obj):
        if isinstance(obj, datetime.date):
            return obj.isoformat()
        return super().default(obj)

def analyzeInvoice(isLocal, loanNumber, pathAndFile):
    logging.info("Analyze Invoice")
    postUrl = FormRecognizerEndPoint + "documentintelligence/documentModels/prebuilt-invoice:analyze?api-version=2023-10-31-preview"
    postUrl = postUrl + "&stringIndexType=utf16CodeUnit&pages=1&queryFields=Loan&features=keyValuePairs%2CqueryFields"

    headers = {
        'Content-Type': 'application/octet-stream',
        'Ocp-Apim-Subscription-Key': FormRecognizerKey
    }

    params = {
        "includeTextDetails": True,
        "pages" : 1,
        "features":["keyValuePairs","queryFields"]

    }

    if (isLocal == "true"):
        with open(pathAndFile, "rb") as f:
            dataBytes = f.read()
        dataDirectory = "../Data/Loan/" + loanNumber + "/"
        destinationPath = dataDirectory + "Python/"
    else:
        blobService = BlobServiceClient.from_connection_string(OpenAiDocConnStr)
        containerName = IncomingContainer
        processedContainer = ProcessedContainer
        blobClient = blobService.get_blob_client(container=containerName, blob=pathAndFile)
        dataBytes = blobClient.download_blob().readall()

    try:
        response = post(url=postUrl, data=dataBytes, headers=headers)
        if response.status_code != 202:
            logging.info("POST Analyze failed")
            return None
        #print("POST analyze succedded", response.headers["Operation-Location"])
        getUrl = response.headers['Operation-Location']
    except Exception as e:
        logging.info("POST analyzed failed" + str(e))
        return None
    
    nTries = 50
    nTry = 0
    waitSec = 6

    while nTry < nTries:
        try:
            getResponse  = get(url=getUrl, headers=headers)
            respJson = json.loads(getResponse.text)
            if (getResponse.status_code != 200):
                print("Invoice Get Failed")
                return None
            status = respJson["status"]
            #print(status)
            if status == "succeeded":
                fileName = os.path.basename(pathAndFile).replace(".pdf", ".json")
                if (isLocal == "true"):
                    with open(destinationPath + fileName, "w") as f:
                        json.dump(respJson, f, indent=4, default=str)
                else:
                    blobService = BlobServiceClient.from_connection_string(OpenAiDocConnStr)
                    pathAndFile = pathAndFile.replace(".pdf", ".json")
                    blobClient = blobService.get_blob_client(container=processedContainer, blob=pathAndFile)
                    blobClient.upload_blob(json.dumps(respJson, indent=4, default=str), overwrite=True)
                return respJson
            if status == "failed":
                logging.info("Analysis Failed")
                return None
            time.sleep(waitSec)
            nTry += 1
        except Exception as e:
            logging.info("Exception during GET" + str(e))
            return None

def processAnalyzeResult(analyzeResults, rowId):
    logging.info("Process Analyze Results")
    paymentDate = ''
    serviceDate = ''
    invoiceNbr = ''
    vendorName = ''
    expenseDesc = ''
    amountPaid = ''
    notes = ''
    loanNumber = ''
    invoiceTotal = ''
    invoiceDate = ''
    filledItems = []
    templateStructure = '{"Row_Id":"<Row_Id>", "Payment_Date":"<Payment_Date>","Service_Date":"<Service_Date>", "Loan_Number": "<Loan_Number>", "Item_Description": "<Item_Description>","Item_Amount": "<Item_Amount>","Invoice_Nbr": "<Invoice_Nbr>", "Invoice_Date": "<Invoice_Date>", "Invoice_Total": "<Invoice_Total>"}'
    for idx, invoice in enumerate(analyzeResults["documents"]):
        for name, field in invoice["fields"].items():
            if name != "Items":
                if name == "VendorName":
                    vendorName = field["content"]
                if name == "InvoiceId":
                    invoiceNbr = field["content"]
                if name == "InvoiceDate" and field["type"] == "date":
                    try:
                        invoiceDate = field["valueDate"]
                    except:
                        invoiceDate = field["content"]
                if name == "InvoiceTotal" and field["type"] == "currency":
                    try:
                        invoiceTotal = field["valueCurrency"]["amount"]
                    except:
                        invoiceTotal = field["content"]
                if name == "CustomerAddress":
                    notes = field["content"]
                if name == "Loan":
                    try:
                        loanNumber = field["content"]
                    except:
                        loanNumber = ''

                #print("...{}: {} has confidence {}".format(name, field.content, field.confidence))

        for idx, item in enumerate(invoice["fields"].get("Items").get("valueArray")):
            #print("...Item #{}".format(idx))
            for name, field in item["valueObject"].items():
                if name == "Amount" and field["type"] == "currency":
                    try:
                        amountPaid = field["valueCurrency"]["amount"]
                    except:
                        amountPaid = field["content"]
                if name == "Date" and field["type"] == "date":
                    try:
                        serviceDate = field["valueDate"]
                    except:
                        serviceDate = field["content"]
                    #print(serviceDate)
                if name == "Description":
                    expenseDesc = field["content"]

            values = {'Row_Id':rowId, 'Payment_Date':paymentDate,'Service_Date': serviceDate, 'Loan_Number': loanNumber, 
                'Item_Description': expenseDesc, 'Item_Amount': Decimal(str(amountPaid)), 'Invoice_Nbr': invoiceNbr.removeprefix("60"),
                'Invoice_Date': invoiceDate, 'Invoice_Total': invoiceTotal}
            filledItem = replaceJsonPlaceHolder(templateStructure,values)
            filledItems.append(filledItem)
            rowId += 1

                #print("......{}: {} has confidence {}".format(name, field.content, field.confidence))

    # for idx, kv in enumerate(analyzeResults["keyValuePairs"]):
    #     if (kv["key"] != None):
    #         #if (kv["key"]["content"] and kv["value"]["content"]):
    #         if (kv["key"]["content"]):
    #             if kv["key"]["content"] == "PaymentDate":
    #                 paymentDate = kv["value"]["content"]
    #             #print("Key...{}: Value...{}".format(kv.key.content, kv.value.content))
    

    #print(values)
    return filledItems

def replaceJsonPlaceHolders(json, values):
  # find all placeholders
  placeholders = re.findall('<[\w ]+>', json)
  assert len(placeholders) == len(values), "Please enter the values of all placeholders."

  # replaces all placeholders with values
  for k, v in values.items():
      placeholder = "<%s>" % k
      json = json.replace(placeholder, v)

  return json

def convertToJsonSerializable(data):  
    if pd.isnull(data):  
        # Convert NaN or NaT to None  
        return None  
    elif isinstance(data, pd.Timestamp):  
        # Convert Timestamp to ISO 8601 format string  
        return data.date().isoformat()
    else:  
        # Return other data types unchanged  
        return data 

# Function to load and parse a JSON file  
def loadJson(file_path):
    with open(file_path, 'r') as file:  
        return json.load(file)

# Function to compare two lists of dictionaries based on key fields  
def compareJsonArray(jsonArray1, jsonArray2, keyFields):  
    #matchingOutput = {"Advances": []}
    #nonMatchingOutput = {"Advances": []}
    matchingObjects = []  
    nonMatchingObjects = []  
  
    # Convert the second JSON array to a dictionary for faster lookup  
    json_dict2 = {tuple(item[key] for key in keyFields): item for item in jsonArray2}
    json_dict1 = {tuple(item[key] for key in keyFields): item for item in jsonArray1}
  
    # Iterate through the first JSON array and compare  
    for item1 in jsonArray1:  
        key = tuple(item1[key] for key in keyFields)  
        item2 = json_dict2.get(key)  
        if item2:  
            # If a matching object is found based on key fields, store it  
            #matchingObjects.append({'object1': item1, 'object2': item2})
            item1.update({'Row_Id_Invoice': item2['Row_Id'], 'Invoice_Date': item2['Invoice_Date'], 'Invoice_Total': item2['Invoice_Total'],
                          'Invoice_Item_Description': item2['Item_Description']})
            matchingObjects.append(item1)
        else:  
            # If no matching object is found, store the non-matching object from the first array  
            nonMatchingObjects.append(item1)  
  
    # Also check for any objects in the second array that didn't match any in the first  
    for item2 in jsonArray2:  
        key = tuple(item2[key] for key in keyFields)  
        if key not in json_dict1:  
            nonMatchingObjects.append(item2)  
  
    #matchingOutput["Advances"] = matchingObjects
    #nonMatchingOutput["Advances"] = nonMatchingObjects
    #return matchingOutput, nonMatchingOutput  
    return matchingObjects, nonMatchingObjects

# Helper function to find the answer to a question

def processStep3(loanNumber, isLocal):
    try:
        if (isLocal == "true"):
            dataDirectory = "../Data/Loan/" + loanNumber + "/"
            jsonArray1 = loadJson(dataDirectory + "Python/" + loanNumber + ".json")
            jsonArray2 = loadJson(dataDirectory + "Python/" + loanNumber + "_FrOut.json")
        else:
            blobService = BlobServiceClient.from_connection_string(OpenAiDocConnStr)
            outputContainer = OutputContainer
            dataDirectory = loanNumber + "/"
            blobName = dataDirectory + loanNumber + ".json"
            blobClient = blobService.get_blob_client(container=outputContainer, blob=blobName)
            jsonArray1 = json.loads(blobClient.download_blob().readall())
            blobName = dataDirectory + loanNumber + "_FrOut.json"
            blobClient = blobService.get_blob_client(container=outputContainer, blob=blobName)
            jsonArray2 = json.loads(blobClient.download_blob().readall())
        
        groundTruthAmount = sum(map(lambda x: int(x['Item_Amount']), jsonArray1))
        scannedInvoiceAmount = sum(map(lambda x: int(x['Item_Amount']), jsonArray2))
        
        matching, nonMatching = compareJsonArray(jsonArray1, jsonArray2, 
                    ['Invoice_Nbr', "Service_Date", "Item_Amount"])
        
        if (isLocal == "true"):
            matchingOutputJson = dataDirectory + "Python/" + loanNumber + "_FuzzyMatching.json"
            nonMatchingOutputJson = dataDirectory + "Python/" + loanNumber + "_FuzzyNonMatching.json"
            with open(matchingOutputJson, 'w') as json_file:
                json_file.write(json.dumps(matching, indent=4))

            with open(nonMatchingOutputJson, 'w') as json_file:
                json_file.write(json.dumps(nonMatching, indent=4))
        else:
            # Save the JSON data to the Blob Storage
            blobService = BlobServiceClient.from_connection_string(OpenAiDocConnStr)
            outputContainer = OutputContainer
            dataDirectory = loanNumber + "/"
            blobName = dataDirectory + loanNumber + "_FuzzyMatching.json"
            blobClient = blobService.get_blob_client(container=outputContainer, blob=blobName)
            blobClient.upload_blob(json.dumps(matching, indent=4), overwrite=True)
            blobName = dataDirectory + loanNumber + "_FuzzyNonMatching.json"
            blobClient = blobService.get_blob_client(container=outputContainer, blob=blobName)
            blobClient.upload_blob(json.dumps(nonMatching, indent=4), overwrite=True)

        statistics = {"Total Source Records": len(jsonArray1), "Total Scanned Invoice Records": len(jsonArray2), "GroundTruthAmount": groundTruthAmount, "ScannedInvoiceAmount": scannedInvoiceAmount, "Matching": len(matching), "NonMatching": len(nonMatching)}
        return statistics
    except Exception as e:
        logging.info("Error in processStep3 : " + str(e))
        return "Processing initial scan of Ground truth and scanned invoice failed : " + str(e)
    
def processStep2(loanNumber, isLocal, corpAdvFolder, invoiceFolder):
    extractedData = []
    sampleOutputDocs = []
    sampleDocs = []
    rowId = 0
    try:
        if (isLocal == "true"):
            dataDirectory = "../Data/Loan/" + loanNumber + "/"

            if not os.path.exists(dataDirectory + corpAdvFolder):
                sourcePath = dataDirectory
            else:
                sourcePath = dataDirectory + corpAdvFolder + "/"

            if not os.path.exists(dataDirectory + invoiceFolder):
                sourcePathInv = dataDirectory
            else:
                sourcePathInv = dataDirectory + invoiceFolder + "/"
                
            destinationPath = dataDirectory + "Python/"
            for file in os.listdir(sourcePath):
                if file.endswith(".pdf"):
                    sampleDocs.append(sourcePath + file)

            for file in os.listdir(sourcePathInv):
                if file.endswith(".pdf"):
                    sampleDocs.append(sourcePathInv + file)

            for file in os.listdir(destinationPath):
                if file.endswith(".json"):
                    sampleOutputDocs.append(destinationPath + file)
        else:
            # Read the data from the Blob Storage
            blobService = BlobServiceClient.from_connection_string(OpenAiDocConnStr)
            containerName = IncomingContainer
            processedContainer = ProcessedContainer
            dataDirectory = loanNumber + "/"
            sourcePath = dataDirectory + corpAdvFolder + "/"
            sourcePathInv = dataDirectory + invoiceFolder + "/"
            sampleDocs = []
            blobClient = blobService.get_blob_client(container=containerName, blob=sourcePath)
            containerClient = blobService.get_container_client(containerName)
            for file in containerClient.list_blobs(name_starts_with=sourcePath):
                if file.name.endswith(".pdf"):
                    sampleDocs.append(file.name)
            for file in containerClient.list_blobs(name_starts_with=sourcePathInv):
                if file.name.endswith(".pdf"):
                    sampleDocs.append(file.name)
        for sampleDoc in sampleDocs:
            # Check if we already have ran the analysis
            if (isLocal == "true"):
                fileName = os.path.basename(sampleDoc).replace(".pdf", ".json")
                if os.path.exists(destinationPath + fileName):
                    logging.info("--------Process Already analyzed Invoice: ", sampleDoc)
                    with open(destinationPath + fileName, "r") as f:
                        invoices = json.load(f)
                    analyzeResults = invoices['analyzeResult']
                    filledItems = processAnalyzeResult(analyzeResults, rowId)
                    for filledItem in filledItems:
                        rowId += 1
                        extractedData.append(ast.literal_eval(json.dumps(filledItem)))
                    continue
                else:
                    logging.info("--------Process Invoice: ", sampleDoc)
                    analyzeInvoice(isLocal, loanNumber, sampleDoc)
                    with open(destinationPath + fileName, "r") as f:
                        invoices = json.load(f)
                    analyzeResults = invoices['analyzeResult']
                    filledItems = processAnalyzeResult(analyzeResults, rowId)
                    for filledItem in filledItems:
                        rowId += 1
                        extractedData.append(ast.literal_eval(json.dumps(filledItem)))
            else:
                blobService = BlobServiceClient.from_connection_string(OpenAiDocConnStr)
                jsonSampleDoc = sampleDoc.replace(".pdf", ".json")
                blobClient = blobService.get_blob_client(container=processedContainer, blob=jsonSampleDoc)
                if blobClient.exists():
                    logging.info(f"--------Process Already analyzed Invoice: {sampleDoc}")
                    invoices = blobClient.download_blob().readall()
                    analyzeResults = json.loads(invoices)['analyzeResult']
                    filledItems = processAnalyzeResult(analyzeResults, rowId)
                    for filledItem in filledItems:
                        rowId += 1
                        extractedData.append(ast.literal_eval(json.dumps(filledItem)))
                    continue
                else:
                    logging.info(f"--------Process Invoice: {sampleDoc}")
                    analyzeInvoice(isLocal, loanNumber, sampleDoc)
                    blobClient = blobService.get_blob_client(container=processedContainer, blob=jsonSampleDoc)
                    invoices = blobClient.download_blob().readall()
                    analyzeResults = json.loads(invoices)['analyzeResult']
                    filledItems = processAnalyzeResult(analyzeResults, rowId)
                    for filledItem in filledItems:
                        rowId += 1
                        extractedData.append(ast.literal_eval(json.dumps(filledItem)))

        updatedOutput = json.dumps(extractedData, indent=4, ensure_ascii=False)
        updatedOutput = updatedOutput.replace("\"{", "{")
        updatedOutput = updatedOutput.replace("}\"", "}")
        updatedOutput = updatedOutput.replace("\\", "")
        tmpDf = pd.read_json(updatedOutput)
        tmpDf['Invoice_Total'] = pd.to_numeric(tmpDf['Invoice_Total'])
        tmpDf = tmpDf.sort_values(['Invoice_Nbr', 'Invoice_Date', "Item_Amount", 'Service_Date'], ascending=False)
        dropDuplicate = tmpDf.drop_duplicates(['Item_Amount','Invoice_Date','Invoice_Nbr'], keep='first')
        if (isLocal == "true"):
            processedOutputJson = dataDirectory + "Python/" + loanNumber + "_FrOut.json"
            processedOutputFullJson = dataDirectory + "Python/" + loanNumber + "_FullFrOut.json"
            json.dumps(tmpDf.to_json(processedOutputFullJson, orient='records'), indent=2)
            json.dumps(dropDuplicate.to_json(processedOutputJson, orient='records'), indent=2)
        else:
            # Save the JSON data to the Blob Storage
            blobService = BlobServiceClient.from_connection_string(OpenAiDocConnStr)
            outputContainer = OutputContainer
            dataDirectory = loanNumber + "/"
            blobName = dataDirectory + loanNumber + "_FrOut.json"
            fullBlobName = dataDirectory + loanNumber + "_FullFrOut.json"
            blobClient = blobService.get_blob_client(container=outputContainer, blob=blobName)
            blobClientFull = blobService.get_blob_client(container=outputContainer, blob=fullBlobName)
            blobClientFull.upload_blob(json.dumps(json.loads(tmpDf.to_json(orient='records')), indent=2), overwrite=True)
            blobClient.upload_blob(json.dumps(json.loads(dropDuplicate.to_json(orient='records')), indent=2), overwrite=True)

        if (isLocal == "true"):
            logging.info("Skip deleting the original files")
        else:
            # Move the original files from the blob Storage
            blobService = BlobServiceClient.from_connection_string(OpenAiDocConnStr)
            containerName = IncomingContainer
            processedContainerName = ProcessedContainer
            for sampleDoc in sampleDocs:
                processedClient = blobService.get_blob_client(container=processedContainerName, blob=sampleDoc)
                processedClient.upload_blob(sampleDoc, overwrite=True)
                blobClient = blobService.get_blob_client(container=containerName, blob=sampleDoc)
                blobClient.delete_blob()

            # Delete if there's anything else left
            containerClient = blobService.get_container_client(containerName)
            dataDirectory = loanNumber + "/"
            for file in containerClient.list_blobs(name_starts_with=dataDirectory):
                processedClient = blobService.get_blob_client(container=processedContainerName, blob=file.name)
                processedClient.upload_blob(file.name, overwrite=True)
                blobClient = blobService.get_blob_client(container=containerName, blob=file.name)
                blobClient.delete_blob()

        return "All invoices processed and analyzed. Total extracted items : " + str(rowId)
    except Exception as e:
        logging.info("Error in processStep2 : " + str(e))
        return "Invoice Analysis failed : " + str(e)
def processStep1(loanNumber, isLocal):
    logging.info("Processing Step 1 - Convert the Excel File")
    try:
        if (isLocal == "true"):
            dataDirectory = "../Data/Loan/" + loanNumber + "/"
            excelFile = dataDirectory + loanNumber + ".xlsx"
            outputJson = dataDirectory + "/Python/" + loanNumber + ".json"

            # Load the Excel file  
            xlsx = pd.ExcelFile(excelFile)  
            
            # Create a dictionary to store the data from each sheet  
            data = {}  
            
            # Iterate through each worksheet in the Excel file  
            for sheetName in xlsx.sheet_names:
                if sheetName != "Advances":
                    continue
                
                df = pd.read_excel(xlsx, sheetName)

                # Apply the conversion function to each cell in the DataFrame  
                df = df.applymap(convertToJsonSerializable)

                # Convert the DataFrame to a dictionary
                if sheetName == "Advances":
                    df = df.rename(columns={'Payment Date': 'Payment_Date', 'Service Date': 'Service_Date', 'Invoice Number': 'Invoice_Nbr', 
                                    'Expense Description': 'Item_Description', 'Amount Paid': 'Item_Amount'})
                    subsetDf = df[['Payment_Date', 'Service_Date', 'Invoice_Nbr', 'Item_Description', 'Item_Amount']]
                    filteredDf = subsetDf[subsetDf["Item_Amount"] > 0]
                    filteredDf = filteredDf.reset_index()
                    filteredDf = filteredDf.rename(columns={"index":"Row_Id"})
                    data = filteredDf.to_dict(orient='records')
                #else:
                #    data[sheetName] = df.to_dict(orient='records')

            # Convert the dictionary to a JSON string 
            jsonData = json.dumps(data, indent=4, ensure_ascii=False)
            
            # Optionally, you can save this JSON data to a file  
            with open(outputJson, 'w') as jsonFile:  
                jsonFile.write(jsonData)  
            return "JSON file has been created: " + outputJson
        else:
            # Read the data from the Blob Storage
            blobService = BlobServiceClient.from_connection_string(OpenAiDocConnStr)
            containerName = IncomingContainer
            outputContainerName = OutputContainer
            dataDirectory = loanNumber + "/"
            excelBlobName = dataDirectory + loanNumber + ".xlsx"
            blobClient = blobService.get_blob_client(container=containerName, blob=excelBlobName)
            data = blobClient.download_blob()
            xlsx = pd.ExcelFile(data.content_as_bytes())
            for sheetName in xlsx.sheet_names:
                if sheetName != "Advances":
                    continue
                df = pd.read_excel(xlsx, sheetName)
                df = df.applymap(convertToJsonSerializable)
                if sheetName == "Advances":
                    df = df.rename(columns={'Payment Date': 'Payment_Date', 'Service Date': 'Service_Date', 'Invoice Number': 'Invoice_Nbr',
                                    'Expense Description': 'Item_Description', 'Amount Paid': 'Item_Amount'})
                    subsetDf = df[['Payment_Date', 'Service_Date', 'Invoice_Nbr', 'Item_Description', 'Item_Amount']]
                    filteredDf = subsetDf[subsetDf["Item_Amount"] > 0]
                    filteredDf = filteredDf.reset_index()
                    filteredDf = filteredDf.rename(columns={"index":"Row_Id"})
                    data = filteredDf.to_dict(orient='records')
            
            # Convert the dictionary to a JSON string 
            jsonData = json.dumps(data, indent=4, ensure_ascii=False)

            # Save the JSON data to the Blob Storage
            blobService = BlobServiceClient.from_connection_string(OpenAiDocConnStr)
            jsonBlobName = dataDirectory + loanNumber + ".json"
            blobClient = blobService.get_blob_client(container=outputContainerName, blob=jsonBlobName)
            blobClient.upload_blob(jsonData, overwrite=True)

            # Delete the original Excel file from the blob Storage
            blobService = BlobServiceClient.from_connection_string(OpenAiDocConnStr)
            containerName = IncomingContainer
            processedContainerName = ProcessedContainer
            processedClient = blobService.get_blob_client(container=processedContainerName, blob=excelBlobName)
            processedClient.upload_blob(excelBlobName, overwrite=True)
            blobClient = blobService.get_blob_client(container=containerName, blob=excelBlobName)
            blobClient.delete_blob()

        return "Excel conversion completed and JSON file " + jsonBlobName + " created: "
    except Exception as e:
        logging.info("Error in processStep1 : " + str(e))
        return "Error processing excel conversion : " + str(e)

def InvoiceSteps(step, loanNumber, isLocal, corpAdvFolder, invoiceFolder):
    try:
        temperature = 0.3
        tokenLength = 1000
    except Exception as e:
        logging.info("Error in InvoiceSteps Open AI : " + str(e))
        return {"data_points": "", "answer": "Exception during finding answers - Error : " + str(e), "thoughts": "", "sources": "", "nextQuestions": "", "error":  str(e)}

    try:
        
        if step == "1":
            logging.info("Calling Step 1")
            step1Response = processStep1(loanNumber, isLocal)
            outputFinalAnswer = {"data_points": '', "answer": step1Response, 
                            "thoughts": '',
                                "sources": '', "nextQuestions": '', "error": ""}
            return outputFinalAnswer
        elif step == "2":
            logging.info("Calling Step 2")
            step2Response = processStep2(loanNumber, isLocal, corpAdvFolder, invoiceFolder)
            outputFinalAnswer = {"data_points": '', "answer": step2Response, 
                            "thoughts": '',
                                "sources": '', "nextQuestions": '', "error": ""}
            return outputFinalAnswer
        elif step == "3":
            logging.info("Calling Step 3")
            step3Response = processStep3(loanNumber, isLocal)
            outputFinalAnswer = {"data_points": '', "answer": step3Response, 
                            "thoughts": '',
                                "sources": '', "nextQuestions": '', "error": ""}
            return outputFinalAnswer
    except Exception as e:
      logging.info("Error in Invoice Steps : " + str(e))
      return {"data_points": "", "answer": "Exception during finding answers - Error : " + str(e), "thoughts": "", "sources": "", "nextQuestions": "", "error":  str(e)}

    #return answer
def TransformValue(step, loanNumber, isLocal, corpAdvFolder, invoiceFolder, record):
    logging.info("Calling Transform Value")
    try:
        recordId = record['recordId']
    except AssertionError  as error:
        return None

    # Validate the inputs
    try:
        assert ('data' in record), "'data' field is required."
        data = record['data']
        assert ('text' in data), "'text' field is required in 'data' object."

    except KeyError as error:
        return (
            {
            "recordId": recordId,
            "errors": [ { "message": "KeyError:" + error.args[0] }   ]
            })
    except AssertionError as error:
        return (
            {
            "recordId": recordId,
            "errors": [ { "message": "AssertionError:" + error.args[0] }   ]
            })
    except SystemError as error:
        return (
            {
            "recordId": recordId,
            "errors": [ { "message": "SystemError:" + error.args[0] }   ]
            })

    try:
        # Getting the items from the values/data/text
        value = data['text']
        answer = InvoiceSteps(step, loanNumber, isLocal, corpAdvFolder, invoiceFolder)
        return ({
            "recordId": recordId,
            "data": answer
            })

    except:
        return (
            {
            "recordId": recordId,
            "errors": [ { "message": "Could not complete operation for record." }   ]
            })
def ComposeResponse(step, loanNumber, isLocal, corpAdvFolder, invoiceFolder, jsonData):
    values = json.loads(jsonData)['values']

    logging.info("Calling Compose Response")
    # Prepare the Output before the loop
    results = {}
    results["values"] = []

    for value in values:
        outputRecord = TransformValue(step, loanNumber, isLocal, corpAdvFolder, invoiceFolder, value)
        logging.info("Output Record : " + str(outputRecord))
        if outputRecord != None:
            results["values"].append(outputRecord)
    return json.dumps(results, ensure_ascii=False)
def main(req: func.HttpRequest, context: func.Context) -> func.HttpResponse:
    logging.info(f'{context.function_name} HTTP trigger function processed a request.')
    if hasattr(context, 'retry_context'):
        logging.info(f'Current retry count: {context.retry_context.retry_count}')

        if context.retry_context.retry_count == context.retry_context.max_retry_count:
            logging.info(
                f"Max retries of {context.retry_context.max_retry_count} for "
                f"function {context.function_name} has been reached")

    try:
        step = req.params.get('step')
        loanNumber= req.params.get('loanNumber')
        isLocal= req.params.get('isLocal')
        corpAdvFolder= req.params.get('corpAdvFolder')
        invoiceFolder= req.params.get('invoiceFolder')
        logging.info("Input parameters : " + step + " " + loanNumber)
        body = json.dumps(req.get_json())
    except ValueError:
        return func.HttpResponse(
             "Invalid body",
             status_code=400
        )

    if body:
        result = ComposeResponse(step, loanNumber, isLocal, corpAdvFolder, invoiceFolder, body)
        return func.HttpResponse(result, mimetype="application/json")
    else:
        return func.HttpResponse(
             "Invalid body",
             status_code=400
        )