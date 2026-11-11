using Microsoft.EntityFrameworkCore;
using OutreachFlow.Models;

namespace OutreachFlow.Data;

public static class DemoData
{
    public static async Task SeedAsync(OutreachDbContext db)
    {
        if (!await db.Workspaces.AnyAsync())
            db.Workspaces.Add(new Workspace());

        if (!await db.Prospects.AnyAsync())
        {
            // Fictional data and reserved .example addresses. Never contact these.
            db.Prospects.AddRange(
                new Prospect { CompanyName = "Demo Fleet Operations", SiteName = "Charlotte site", Website = "https://demo-fleet.example", ContactEmail = "vendor@demo-fleet.example" },
                new Prospect { CompanyName = "Example Warehouse Group", SiteName = "Southwest branch", Website = "https://example-warehouse.example", ContactEmail = "procurement@example-warehouse.example" }
            );
        }
        await db.SaveChangesAsync();
    }
}
