using Microsoft.EntityFrameworkCore;
using Warp.Core.Handlers;

namespace Warp.Core.Handlers;

public class OrderConfirmationCommand : IJobHandler<OrderConfirmationRequest>
{
    private readonly TestContext _context;

    public OrderConfirmationCommand(TestContext context)
    {
        _context = context;
    }

    public async Task HandleAsync(OrderConfirmationRequest message, CancellationToken cancellationToken)
    {
        var emailLog = await _context.EmailLogs
            .Where(x => x.Id == message.EmailLogId)
            .FirstAsync(cancellationToken: cancellationToken);

        emailLog.ProcessedTime = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
    }
}

public class OrderConfirmationRequest : IJob
{
    public int EmailLogId { get; set; }
}
