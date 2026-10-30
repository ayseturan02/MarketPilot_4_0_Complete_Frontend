using System.Globalization;
using System.Text;

namespace MarketPilot.Web.Local;

public static class ProductCsv
{
    public static byte[] Create(IEnumerable<Product> products)
    {
        static string Escape(string value) => "\"" +
            (value.Length > 0 && "=+-@\t\r".Contains(value[0]) ? "'" + value : value)
                .Replace("\"", "\"\"") + "\"";
        var rows = new List<string> { "Id,Ürün,SKU,Kategori,Fiyat,Stok,Durum" };
        rows.AddRange(products.Select(product => string.Join(",",
            Escape(product.Id), Escape(product.Name), Escape(product.Sku), Escape(product.Category),
            product.PriceKnown?product.Price.ToString(CultureInfo.InvariantCulture):"",
            product.Stock.ToString(CultureInfo.InvariantCulture), Escape(product.Status))));
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(string.Join("\r\n", rows))).ToArray();
    }
}
