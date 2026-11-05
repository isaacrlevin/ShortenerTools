using Azure;
using Azure.Communication.Email;
using Microsoft.Extensions.Logging;
using System.Text;

namespace ShortenerTools.Core.Domain
{
    public class EmailService
    {
        private EmailClient _emailClient;
        private readonly ILogger _logger;
        private readonly ShortenerSettings _settings;
        public EmailService(ILoggerFactory loggerFactory, ShortenerSettings settings)
        {
            _logger = loggerFactory.CreateLogger<EmailService>();
            _settings = settings;

            // Do not create an SDK client until an allowed send actually needs it.
        }

        private string StackTraceToString(Exception ex)
        {
            StringBuilder stringBuilder = new StringBuilder();
            foreach (var line in ex.StackTrace.Split(Environment.NewLine))
            {
               stringBuilder.AppendLine($"    {line}");
            }
            return stringBuilder.ToString();
        }

        public async Task SendTwitterIntentEmail(string subject, string intentUrl, string tweetText)
        {
            if (!_settings.ExternalPostingAllowed)
            {
                return;
            }
            _emailClient ??= new EmailClient(_settings.COMMUNICATION_SERVICES_CONNECTION_STRING);
            var hashtags = System.Text.RegularExpressions.Regex.Matches(tweetText, @"#\w+")
                               .Select(m => m.Value)
                               .Distinct(StringComparer.OrdinalIgnoreCase)
                               .ToList();

            string hashtagSection = hashtags.Any()
                ? $"<p><strong>Hashtags detected:</strong> {string.Join(" ", hashtags.Select(h => System.Net.WebUtility.HtmlEncode(h)))}</p>"
                : string.Empty;

            string htmlContent = $@"
                <html>
                <body>
                    <h2>X (Twitter) Post Ready to Share</h2>
                    <p><strong>Tweet text:</strong></p>
                    <blockquote style=""border-left:4px solid #1DA1F2;padding-left:12px;white-space:pre-wrap;"">{System.Net.WebUtility.HtmlEncode(tweetText)}</blockquote>
                    {hashtagSection}
                    <p>
                        <a href=""{intentUrl}"" style=""display:inline-block;background:#1DA1F2;color:#fff;padding:10px 20px;text-decoration:none;border-radius:4px;font-size:1.1em;font-weight:bold;"">
                            &#x1F426; Post on X / Twitter
                        </a>
                    </p>
                    <p style=""word-break:break-all;color:#555;""><small>{System.Net.WebUtility.HtmlEncode(intentUrl)}</small></p>
                </body>
                </html>";

            EmailSendOperation emailSendOperation = await _emailClient.SendAsync(
                Azure.WaitUntil.Started,
                _settings.EmailFrom,
                _settings.EmailTo,
                subject,
                htmlContent);

            try
            {
                while (true)
                {
                    await emailSendOperation.UpdateStatusAsync();
                    if (emailSendOperation.HasCompleted)
                    {
                        break;
                    }
                    await Task.Delay(100);
                }

                if (emailSendOperation.HasValue)
                {
                    _logger.LogInformation($"Twitter intent email queued for delivery. Status = {emailSendOperation.Value.Status}");
                }
            }
            catch (RequestFailedException exception)
            {
                _logger.LogError($"Twitter intent email send failed with Code = {exception.ErrorCode} and Message = {exception.Message}");
            }

            string operationId = emailSendOperation.Id;
            _logger.LogInformation($"Twitter intent email operation id = {operationId}");
        }

        public async Task SendExceptionEmail(string subject, Exception ex, string message = "")
        {
            if (!_settings.ExternalPostingAllowed || ex == null)
            {
                return;
            }
            _emailClient ??= new EmailClient(_settings.COMMUNICATION_SERVICES_CONNECTION_STRING);

            if (!string.IsNullOrEmpty(message))
            {
                message = $"<p>{message}</p>";
            }   

            string htmlContent = $@"
                <html>
                <body>
                    {message}
                    <h1>Exception Type: {ex.GetType().Name}</h1>
                    <p>Exception Message: {ex.Message}</p>
                    <p>StackTrace: {StackTraceToString(ex)}</p>
                </body>
                </html>";

            /// Send the email message with WaitUntil.Started
            EmailSendOperation emailSendOperation = await _emailClient.SendAsync(
                Azure.WaitUntil.Started,
                _settings.EmailFrom,
                _settings.EmailTo,
                subject,
                htmlContent);

            /// Call UpdateStatus on the email send operation to poll for the status
            /// manually.
            try
            {
                while (true)
                {
                    await emailSendOperation.UpdateStatusAsync();
                    if (emailSendOperation.HasCompleted)
                    {
                        break;
                    }
                    await Task.Delay(100);
                }

                if (emailSendOperation.HasValue)
                {
                    _logger.LogInformation($"Email queued for delivery. Status = {emailSendOperation.Value.Status}");
                }
            }
            catch (RequestFailedException exception)
            {
               _logger.LogError($"Email send failed with Code = {exception.ErrorCode} and Message = {exception.Message}");
            }

            /// Get the OperationId so that it can be used for tracking the message for troubleshooting
            string operationId = emailSendOperation.Id;
            _logger.LogInformation($"Email operation id = {operationId}");
        }
    }
}
