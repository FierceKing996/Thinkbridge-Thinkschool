namespace Refactor_controller.Services;

public interface IEmailSender
{
    Task SendOrderConfirmationAsync(string email, int orderId, decimal total, CancellationToken cancellationToken);
}

public class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;

    public LoggingEmailSender(ILogger<LoggingEmailSender> logger)
    {
        _logger = logger;
    }

    public Task SendOrderConfirmationAsync(string email, int orderId, decimal total, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new InvalidOperationException("No email on file");
        }

        _logger.LogInformation("Emailing {Email} about order {OrderId} totalling {Total:C}", email, orderId, total);
        return Task.CompletedTask;
    }
}
