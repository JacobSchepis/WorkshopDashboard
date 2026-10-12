using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace WorkshopDashboard.Services.External.Monday
{
    /// <summary>
    /// Board metadata only (id/name) — used to populate a board picker before
    /// committing to a full items query.
    /// </summary>
    public class MondayBoard
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    /// <summary>A board group (the collapsible sub-tables you see in monday's UI, e.g.
    /// "Open Orders" / "Order Finalisation") — pass its Id to GetBoardDetailAsync to
    /// scope the item fetch instead of pulling the whole board.</summary>
    public class MondayGroup
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
    }

    /// <summary>Column definition from a board — Id is the key used in MondayItem.ColumnValues.</summary>
    public class MondayColumn
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Type { get; set; } = "";
    }

    public class MondayItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";

        /// <summary>Column id -> display text. Look up by MondayColumn.Id (e.g. from a
        /// board's "PO" or "Tracking" column) to build feature-specific logic later.</summary>
        public Dictionary<string, string> ColumnValues { get; set; } = new();
    }

    public class MondayBoardDetail
    {
        public List<MondayColumn> Columns { get; set; } = new();
        public List<MondayItem> Items { get; set; } = new();
    }

    /// <summary>Read access to monday.com boards — the API normally, a made-up purchasing
    /// board in demo mode (Demo/DemoMonday).</summary>
    public interface IMondayClient
    {
        Task<List<MondayBoard>> GetBoardsAsync(CancellationToken ct = default);
        Task<List<MondayGroup>> GetGroupsAsync(string boardId, CancellationToken ct = default);
        Task<MondayBoardDetail> GetBoardDetailAsync(string boardId, IReadOnlyCollection<string>? groupIds = null, CancellationToken ct = default);
    }

    public class MondayService : IMondayClient
    {
        private const string ApiUrl = "https://api.monday.com/v2";

        private readonly HttpClient _httpClient;
        private readonly ILogger<MondayService> _logger;
        private readonly string _apiToken;

        public MondayService(HttpClient httpClient, IOptions<MondayOptions> options, ILogger<MondayService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _apiToken = options.Value.ApiToken;

            if (string.IsNullOrWhiteSpace(_apiToken))
                _logger.LogWarning("[Monday] Monday:ApiToken isn't set — board queries will be skipped");
        }

        /// <summary>Runs a raw GraphQL query against the monday.com v2 API. Returns the
        /// parsed "data" element, or null if the token is missing, the call fails, or the
        /// response contains GraphQL errors (all logged).</summary>
        private async Task<JsonElement?> QueryAsync(string query, object? variables, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_apiToken))
            {
                _logger.LogWarning("Monday API token missing; skipping query.");
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Headers.TryAddWithoutValidation("Authorization", _apiToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var payload = variables is null
                ? new { query }
                : (object)new { query, variables };

            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception calling Monday API");
                return null;
            }

            using (response)
            {
                var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Monday API HTTP {StatusCode}: {Body}", (int)response.StatusCode, raw);
                    return null;
                }

                using var doc = JsonDocument.Parse(raw);

                if (doc.RootElement.TryGetProperty("errors", out var errors))
                {
                    _logger.LogError("Monday API returned errors: {Errors}", errors.ToString());
                    return null;
                }

                if (!doc.RootElement.TryGetProperty("data", out var data))
                {
                    return null;
                }

                return data.Clone();
            }
        }

        public async Task<List<MondayBoard>> GetBoardsAsync(CancellationToken ct = default)
        {
            const string query = "query { boards (limit: 100) { id name } }";

            var boards = new List<MondayBoard>();

            var data = await QueryAsync(query, null, ct).ConfigureAwait(false);
            if (data is not { } root || !root.TryGetProperty("boards", out var boardsEl))
                return boards;

            foreach (var b in boardsEl.EnumerateArray())
            {
                boards.Add(new MondayBoard
                {
                    Id = b.GetProperty("id").GetString() ?? "",
                    Name = b.GetProperty("name").GetString() ?? ""
                });
            }

            return boards;
        }

        // Safety cap on pagination — 200 pages * 100 items = 20,000 items. A board would
        // have to be pathologically large (or next_items_page stuck returning a cursor
        // forever) to hit this; it exists so a bug can't turn into an infinite loop.
        private const int MaxItemPages = 200;

        public async Task<List<MondayGroup>> GetGroupsAsync(string boardId, CancellationToken ct = default)
        {
            const string query = @"
                query ($boardId: [ID!]) {
                  boards (ids: $boardId) {
                    groups { id title }
                  }
                }";

            var groups = new List<MondayGroup>();

            var data = await QueryAsync(query, new { boardId = new[] { boardId } }, ct).ConfigureAwait(false);
            if (data is not { } root
                || !root.TryGetProperty("boards", out var boardsEl)
                || boardsEl.GetArrayLength() == 0)
            {
                return groups;
            }

            foreach (var g in boardsEl[0].GetProperty("groups").EnumerateArray())
            {
                groups.Add(new MondayGroup
                {
                    Id = g.GetProperty("id").GetString() ?? "",
                    Title = g.GetProperty("title").GetString() ?? ""
                });
            }

            return groups;
        }

        /// <summary>Fetches board columns plus every item, optionally scoped to specific
        /// groups (e.g. "Open Orders") instead of the whole board — a multi-thousand-item
        /// board is otherwise both slow to pull and mostly irrelevant to a live dashboard.
        /// Paginates via next_items_page until each requested scope is exhausted.</summary>
        public async Task<MondayBoardDetail> GetBoardDetailAsync(string boardId, IReadOnlyCollection<string>? groupIds = null, CancellationToken ct = default)
        {
            var scoped = groupIds is { Count: > 0 };

            var firstPageQuery = scoped ? @"
                query ($boardId: [ID!], $groupIds: [String!]) {
                  boards (ids: $boardId) {
                    columns { id title type }
                    groups (ids: $groupIds) {
                      items_page (limit: 100) {
                        cursor
                        items {
                          id
                          name
                          column_values { id text type }
                        }
                      }
                    }
                  }
                }" : @"
                query ($boardId: [ID!]) {
                  boards (ids: $boardId) {
                    columns { id title type }
                    items_page (limit: 100) {
                      cursor
                      items {
                        id
                        name
                        column_values { id text type }
                      }
                    }
                  }
                }";

            const string nextPageQuery = @"
                query ($cursor: String!) {
                  next_items_page (limit: 100, cursor: $cursor) {
                    cursor
                    items {
                      id
                      name
                      column_values { id text type }
                    }
                  }
                }";

            var detail = new MondayBoardDetail();

            var variables = scoped
                ? new { boardId = new[] { boardId }, groupIds = groupIds!.ToArray() }
                : (object)new { boardId = new[] { boardId } };

            var data = await QueryAsync(firstPageQuery, variables, ct).ConfigureAwait(false);
            if (data is not { } root
                || !root.TryGetProperty("boards", out var boardsEl)
                || boardsEl.GetArrayLength() == 0)
            {
                return detail;
            }

            var board = boardsEl[0];

            foreach (var c in board.GetProperty("columns").EnumerateArray())
            {
                detail.Columns.Add(new MondayColumn
                {
                    Id = c.GetProperty("id").GetString() ?? "",
                    Title = c.GetProperty("title").GetString() ?? "",
                    Type = c.GetProperty("type").GetString() ?? ""
                });
            }

            // Each scope (the board itself, or each requested group) has its own items_page
            // and its own pagination cursor — collect starting cursors first, then drain them.
            var cursors = new List<string?>();

            if (scoped)
            {
                foreach (var g in board.GetProperty("groups").EnumerateArray())
                {
                    var itemsPage = g.GetProperty("items_page");
                    AddItems(detail, itemsPage.GetProperty("items"));
                    cursors.Add(itemsPage.TryGetProperty("cursor", out var cur) && cur.ValueKind == JsonValueKind.String ? cur.GetString() : null);
                }
            }
            else
            {
                var itemsPage = board.GetProperty("items_page");
                AddItems(detail, itemsPage.GetProperty("items"));
                cursors.Add(itemsPage.TryGetProperty("cursor", out var cur) && cur.ValueKind == JsonValueKind.String ? cur.GetString() : null);
            }

            for (var i = 0; i < cursors.Count; i++)
            {
                var cursor = cursors[i];

                for (var page = 0; cursor is not null && page < MaxItemPages; page++)
                {
                    var nextData = await QueryAsync(nextPageQuery, new { cursor }, ct).ConfigureAwait(false);
                    if (nextData is not { } nextRoot || !nextRoot.TryGetProperty("next_items_page", out var nextPageEl))
                    {
                        _logger.LogWarning("Monday next_items_page call failed part-way through pagination; returning {Count} items collected so far.", detail.Items.Count);
                        break;
                    }

                    AddItems(detail, nextPageEl.GetProperty("items"));

                    cursor = nextPageEl.TryGetProperty("cursor", out var nextCur) && nextCur.ValueKind == JsonValueKind.String
                        ? nextCur.GetString()
                        : null;
                }
            }

            return detail;
        }

        private static void AddItems(MondayBoardDetail detail, JsonElement itemsEl)
        {
            foreach (var itemEl in itemsEl.EnumerateArray())
            {
                var item = new MondayItem
                {
                    Id = itemEl.GetProperty("id").GetString() ?? "",
                    Name = itemEl.GetProperty("name").GetString() ?? ""
                };

                foreach (var cv in itemEl.GetProperty("column_values").EnumerateArray())
                {
                    var columnId = cv.GetProperty("id").GetString() ?? "";
                    var text = cv.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    item.ColumnValues[columnId] = text;
                }

                detail.Items.Add(item);
            }
        }
    }
}
