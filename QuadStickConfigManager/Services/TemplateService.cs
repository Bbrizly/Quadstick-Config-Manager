using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using CsvHelper;
using QSCM.Models;

namespace QSCM.Services
{
    public class TemplateService
    {
        // This is the E-key for the *index* sheet listing every template.
        const string IndexKey = 
          "2PACX-1vTdyPHsW5dHAgR8DKwQ3hB9hAF1SnrIrYsCt6qvEsPSWB7MxvIVyGFVNQCgD_RcRQRYB8_ncXCYB_EI";
        readonly HttpClient _http = new();

        /// Fetches the index and returns every template’s metadata.
        public async Task<List<TemplateInfo>> LoadIndexAsync()
        {
            var url = 
              $"https://docs.google.com/spreadsheets/d/e/{IndexKey}/pub?output=csv&gid=1483029791";
            await using var stream = await _http.GetStreamAsync(url);
            using var reader = new StreamReader(stream);
            using var csv    = new CsvReader(reader, CultureInfo.InvariantCulture);

            // We assume the sheet has exactly 3 columns in order:
            // [0]=Name, [1]=FileId, [2]=CsvFilename
            var records = new List<TemplateInfo>();
            await foreach (var row in csv.GetRecordsAsync<dynamic>())
            {
                var arr = ((IDictionary<string, object>)row)
                          .Values
                          .Select(v => v?.ToString() ?? "")
                          .ToArray();
                if (arr.Length < 3) continue;
                records.Add(new TemplateInfo
                {
                    Name        = arr[0],
                    FileId      = arr[1],
                    CsvFilename = arr[2]
                });
            }
            return records;
        }

        /// Given one template’s FileId, download *its* CSV.
        public async Task<Profile> LoadProfileAsync(TemplateInfo info)
        {
            // Construct the standard “export as CSV” URL for a Google sheet:
            var url = 
              $"https://docs.google.com/spreadsheets/d/{info.FileId}/export?format=csv";
            await using var stream = await _http.GetStreamAsync(url);
            var rows = CsvConverter.FromCsv(stream);
            return new Profile(info.Name, rows);
        }
    }
}
