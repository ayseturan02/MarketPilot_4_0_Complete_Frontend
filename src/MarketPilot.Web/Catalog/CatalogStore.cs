using MarketPilot.Web.Catalog;
using System.Text.Json;
public partial class DemoStore
{
    // Callbacks project only the requested page while holding the same lock as legacy routes.
    public T CatalogRead<T>(Func<Database, T> reader)
    {
        lock (_gate)
            return reader(_data);
    }
}
