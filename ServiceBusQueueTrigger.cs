using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Dapper;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Npgsql;

namespace EventManagementFunctionApp
{
    public class ServiceBusQueueTrigger
    {
        private readonly ILogger<ServiceBusQueueTrigger> _logger;
        private readonly string? _connectionString;
        private readonly GraphServiceClient _graphClient;
        private readonly string? _fromEmail;

        public ServiceBusQueueTrigger(ILogger<ServiceBusQueueTrigger> logger, IConfiguration configuration)
        {
            _logger = logger;
            _connectionString = configuration["DBConnectionString"];

            var clientId = configuration["GraphClientId"];
            var tenantId = configuration["GraphTenantId"];
            var clientSecret = configuration["GraphClientSecret"];
            _fromEmail = configuration["FromEmail"];

            var clientSecretCredential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            _graphClient = new GraphServiceClient(clientSecretCredential);

        }

        [Function(nameof(ServiceBusQueueTrigger))]
        public async Task Run(
            [ServiceBusTrigger("appqueue", Connection = "eventmanagement2024_SERVICEBUS")]
            ServiceBusReceivedMessage message,
            ServiceBusMessageActions messageActions)
        {
            _logger.LogInformation("Message ID: {MessageId}, Body: {Body}, Content-Type: {ContentType}", message.MessageId, message.Body, message.ContentType);
            var registrationDto = System.Text.Json.JsonSerializer.Deserialize<EventRegistrationDto>(message.Body.ToString());

            try
            {
                if (registrationDto?.Action == "Register")
                {
                    await RegisterEventAsync(registrationDto.EventId!, registrationDto.UserId!);
                    var userPrincipalName = await GetUserPrincipalNameAsync(registrationDto.UserId!);
                    await SendEmailAsync(userPrincipalName, "Registration Confirmation", "You have successfully registered for the event.");
                    _logger.LogInformation("Action: Register, UserId: {UserId}, EventId: {EventId}", registrationDto.UserId, registrationDto.EventId);
                }
                else if (registrationDto?.Action == "Unregister")
                {
                    await UnregisterEventAsync(registrationDto.EventId!, registrationDto.UserId!);
                    var userPrincipalName = await GetUserPrincipalNameAsync(registrationDto.UserId!);
                    await SendEmailAsync(userPrincipalName, "Unregistration Confirmation", "You have successfully unregistered from the event.");
                    _logger.LogInformation("Action: Unregister, UserId: {UserId}, EventId: {EventId}", registrationDto.UserId, registrationDto.EventId);
                }

                await messageActions.CompleteMessageAsync(message);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error processing message: {ErrorMessage}", ex.Message);
            }
        }

        private async Task RegisterEventAsync(string eventId, string userId)
        {
            using (var connection = new NpgsqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        var eventGuid = Guid.Parse(eventId);
                        var eventQuery = "SELECT * FROM \"Events\" WHERE \"Id\" = @EventId FOR UPDATE";
                        var eventDetails = await connection.QuerySingleOrDefaultAsync(eventQuery, new { EventId = eventGuid }, transaction);

                        if (eventDetails == null)
                        {
                            throw new Exception("Event not found.");
                        }

                        var parameters = new { EventId = eventGuid, UserId = userId };
                        var insertRegistrationQuery = "INSERT INTO \"EventRegistrations\" (\"EventId\", \"UserId\", \"Action\") VALUES (@EventId, @UserId, 'Register')";

                        await connection.ExecuteAsync(insertRegistrationQuery, parameters, transaction);

                        transaction.Commit();
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        _logger.LogError("Error during registration: {ErrorMessage}", ex.Message);
                        throw;
                    }
                }
            }
        }

        private async Task UnregisterEventAsync(string eventId, string userId)
        {
            using (var connection = new NpgsqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        var parameters = new { EventId = eventId, UserId = userId };
                        var deleteRegistrationQuery = "DELETE FROM \"EventRegistrations\" WHERE \"EventId\" = @EventId AND \"UserId\" = @UserId";
                        await connection.ExecuteAsync(deleteRegistrationQuery, parameters, transaction);

                        var insertUnregisterActionQuery = "INSERT INTO \"EventRegistrations\" (\"EventId\", \"UserId\", \"Action\") VALUES (@EventId, @UserId, 'Unregister')";
                        await connection.ExecuteAsync(insertUnregisterActionQuery, parameters, transaction);

                        transaction.Commit();
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        _logger.LogError("Error during unregistration: {ErrorMessage}", ex.Message);
                        throw;
                    }
                }
            }
        }


        private async Task<string> GetUserPrincipalNameAsync(string userId)
        {
            var user = await _graphClient.Users[userId].GetAsync();
            var userMail = user?.Mail;
            if (string.IsNullOrEmpty(userMail))
            {
                throw new Exception("User not provide information about email adress");
            }
            return userMail;
        }

        private async Task SendEmailAsync(string userMail, string subject, string message)
        {
            var emailMessage = new Message
            {
                Subject = subject,
                Body = new ItemBody
                {
                    ContentType = BodyType.Html,
                    Content = $"<strong>{message}</strong>"
                },
                ToRecipients = new List<Recipient>
        {
            new Recipient
            {
                EmailAddress = new EmailAddress
                {
                    Address = userMail
                }
            }
        }
            };

            var sendMailPostRequestBody = new Microsoft.Graph.Users.Item.SendMail.SendMailPostRequestBody
            {
                Message = emailMessage,
                SaveToSentItems = false
            };

            try
            {
                await _graphClient.Users[_fromEmail]
                    .SendMail
                    .PostAsync(sendMailPostRequestBody);

                _logger.LogInformation("Email sent to {UserMail}", userMail);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to send email to {UserMail}. Error: {ErrorMessage}", userMail, ex.Message);
            }
        }

        public class EventRegistrationDto
        {
            public string? EventId { get; set; }
            public string? UserId { get; set; }
            public string? Action { get; set; }
        }
    }
}

