using Microsoft.EntityFrameworkCore;
using Warp.Core.Handlers;
using Warp.Core.Helper;
using Warp.Test.Shared.Entities;

namespace Warp.Core.Handlers;

public class ScheduleCustomerSignupRequest : IJob
{
    public string? Email { get; set; }

    public DateTime ScheduleTime { get; set; }
}

public class ScheduleCustomerSignupCommand : IJobHandler<ScheduleCustomerSignupRequest>
{
    private readonly TestContext _context;
    private readonly IPublisher _publisher;

    public ScheduleCustomerSignupCommand(TestContext context, IPublisher publisher)
    {
        _context = context;
        _publisher = publisher;
    }

    public async Task HandleAsync(ScheduleCustomerSignupRequest message, CancellationToken cancellationToken)
    {
        var registration = new Registration
        {
            Email = message.Email,
        };

        _context.Registrations.Add(registration);

        var emailLog = new EmailLog
        {
            Email = message.Email,
            Body = "Test email",
            Subject = "Test subject",
        };

        _context.EmailLogs.Add(emailLog);

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        var sendEmailRequest = new OrderConfirmationRequest
        {
            EmailLogId = emailLog.Id,
        };

        for (var i = 0; i < 10; i++)
        {
            // Original code, works fine with one parameter
            await _publisher.Schedule(sendEmailRequest, message.ScheduleTime);

            // JobData parameters
            var jobParams = new JobParameters
            {
                ScheduleTime = message.ScheduleTime,
                Queue = "high",
            };
            await _publisher.Enqueue(sendEmailRequest, jobParams);
        }

        await _context.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }
}
