using Microsoft.AspNetCore.Components;
using WorkshopDashboard.Services.External.Jms;

namespace WorkshopDashboard.Components.Helpers;

/// <summary>
/// Base component for pages that use JMS. Pages do NOT get a Jms client directly — they
/// inject the specific lookup/service class they need, and that service resolves its own
/// client (JmsReadSession for reads, JmsUserSession for writes). This is what keeps raw JMS
/// queries (Jms.PostQueryForCsvAsync + JmsQueries.*) out of page code entirely.
///
/// It no longer checks the user's JMS session on load or logs anyone out: app login is
/// separate from JMS now (see UserAccountService), an expired JMS session is re-established
/// automatically, and the nav bar's JmsStatusIndicator shows when JMS itself is the problem.
///
/// Usage in pages:
///     @inherits JmsComponentBase
///     @inject SomeJobLookupService Jobs
/// </summary>
public class JmsComponentBase : ComponentBase
{
    [Inject] protected JmsUserSession JmsSession { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;
}
