using System.Net;
using System.Text;
using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

public class LinkedDriveClientTests
{
    sealed class CaptureHandler : HttpMessageHandler
    {
        readonly Func<HttpRequestMessage, string> _response;
        public Uri? LastUri { get; private set; }
        public string LastBody { get; private set; } = "";

        public CaptureHandler(Func<HttpRequestMessage, string> response) => _response = response;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response(request), Encoding.UTF8, "application/json"),
            };
        }
    }

    static LinkedDriveClient Client(CaptureHandler handler) =>
        new(handler, _ => Task.FromResult("access-token"));

    [Fact]
    public async Task Metadata_get_is_Shared_Drive_safe()
    {
        var handler = new CaptureHandler(_ => """
        {
          "id":"file","name":"profile","mimeType":"application/vnd.google-apps.spreadsheet",
          "version":"7","modifiedTime":"2026-08-30T00:00:00Z","trashed":false,
          "capabilities":{"canEdit":true,"canModifyContent":true,"canDownload":true}
        }
        """);

        var file = await Client(handler).GetFileMetadataAsync("file");

        Assert.Equal("7", file.Version);
        Assert.Contains("supportsAllDrives=true", handler.LastUri!.Query);
    }

    [Fact]
    public async Task Cell_write_is_surgical_and_uses_numeric_sheet_identity()
    {
        var handler = new CaptureHandler(_ => "{}");
        var client = Client(handler);

        await client.UpdateCellsAsync("book", new[]
        {
            new LinkedSheetCellUpdate(42, 5, 2, "=literal-not-a-formula"),
        });

        Assert.Contains("\"sheetId\":42", handler.LastBody);
        Assert.Contains("\"startRowIndex\":5", handler.LastBody);
        Assert.Contains("\"startColumnIndex\":2", handler.LastBody);
        Assert.Contains("\"stringValue\":\"=literal-not-a-formula\"", handler.LastBody);
        Assert.DoesNotContain("deleteSheet", handler.LastBody);
        Assert.DoesNotContain("addSheet", handler.LastBody);
        Assert.DoesNotContain("batchClear", handler.LastBody);
    }

    [Fact]
    public async Task Formula_projects_calculated_value_but_stays_marked_read_only()
    {
        var handler = new CaptureHandler(_ => """
        {
          "sheets":[{
            "properties":{"sheetId":9,"title":"Mode 1"},
            "data":[{"startRow":0,"startColumn":0,"rowData":[{"values":[{
              "userEnteredValue":{"formulaValue":"=A2"},
              "effectiveValue":{"stringValue":"button_a"},
              "formattedValue":"button_a"
            }]}]}]
          }]
        }
        """);

        var grid = await Client(handler).ReadGridAsync("book", 9, 10, 12);
        var cell = grid.Rows[0][0];

        Assert.True(cell.IsFormula);
        Assert.Equal("=A2", cell.UserValue);
        Assert.Equal("button_a", cell.TextForProfile);
    }

    [Fact]
    public async Task Linked_marker_is_private_app_metadata_not_a_sheet_edit()
    {
        var handler = new CaptureHandler(_ => "{}");

        await Client(handler).MarkAsLinkedProfileAsync("file");

        Assert.Contains("/drive/v3/files/file", handler.LastUri!.AbsoluteUri);
        Assert.Contains("supportsAllDrives=true", handler.LastUri.Query);
        Assert.Contains("\"qcmDocumentKind\":\"linked-profile\"", handler.LastBody);
        Assert.DoesNotContain("spreadsheets", handler.LastUri.AbsoluteUri);
    }
}
