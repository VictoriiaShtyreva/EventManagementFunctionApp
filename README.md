# Event Management Function App

## Overview

This Azure Function processes event registrations and unregistrations from a Service Bus queue. It ensures that the operations are executed in FIFO order and performs transactions to update event properties. Additionally, the function updates the registration status in a PostgreSQL database and triggers necessary actions such as sending confirmation emails using Microsoft Graph.

## Features

- Processes registration and unregistration messages from the Service Bus queue.
- Ensures FIFO order processing.
- Updates event properties in a PostgreSQL database using Dapper.
- Sends confirmation emails to users using Microsoft Graph.
- Handles errors and logs detailed information for troubleshooting.

## Configuration

The following configuration settings are required in your local.settings.json or Azure Function App configuration:

```
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "ServiceBusConnection": "<Service_Bus_Connection_String>",
    "DBConnectionString": "<PostgreSQL_Connection_String>",
    "GraphClientId": "<Azure_AD_Client_ID>",
    "GraphTenantId": "<Azure_AD_Tenant_ID>",
    "GraphClientSecret": "<Azure_AD_Client_Secret>",
    "FromEmail": "<Email_Address_For_Sending_Emails>"
  }
}
```

## Running the Function App

To run the Azure Function locally:

- Ensure that the required configuration settings are provided in local.settings.json.
- Start the function host using the Azure Functions Core Tools:

```
func start
```

## Deployment

To deploy the Azure Function to Azure:

- Delete the bin and obj folders in the project directory.
- Open a command line or terminal.
- Run the following command to publish the function to Azure:

```
func azure functionapp publish EventManagementFunctionApp --dotnet-isolated
```
