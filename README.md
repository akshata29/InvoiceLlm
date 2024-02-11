
# Overview

Sample C# Code to run the Document Intelligence API to extract the invoice metadata and perform the Reconciliation process against the ground truth.

## Prerequisites

Ensure that you have the folder with the following structure:
    
    ```Data
    ├───Loan
    │   ├───GroundTruth (LoanNumber) - This is where you will host your <LoanNumber>.xsls file.
    │   └───CORPADV (LoanNumber) - This is where you will host your <LoanNumber>.pdf file for Advances.
    │   └───INVOICE (LoanNumber) - This is where you will host your <LoanNumber>.pdf file for Invoices.
    ```

## Getting Started

1. Clone the repository.
2. Open the folder in Visual Studio Code.
3. Setup the Data directory
4. Create the appropriate settings file (as per the sample settings file) for each azure function and configure the settings.
5. Run the Analyze Invoice to extract the raw metadata by invoking the Document Intelligence API.
6. Run the Reconcile to compare the extracted metadata against the ground truth (using FUZZY matching).
7. Run the ReconcileLlm Process to compare the extracted metadata against the ground truth (using LLM).

## Architecture

![Invoice Llm](/assets/InvoiceLlm.png)