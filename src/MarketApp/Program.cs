using Microsoft.Data.Sqlite;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);
var dbPath = Path.Combine(builder.Environment.ContentRootPath, "market.db");
var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
builder.Services.AddSingleton(new MarketStore(connectionString));
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
var store = app.Services.GetRequiredService<MarketStore>();
store.Initialize();

app.MapGet("/api/dashboard", () => Results.Ok(store.Dashboard()));
app.MapGet("/api/categories", () => Results.Ok(store.Categories()));
app.MapPost("/api/categories", (CategoryInput input) => Safe(() => store.AddCategory(input)));
app.MapGet("/api/products", (string? search, int? categoryId) => Results.Ok(store.Products(search, categoryId)));
app.MapPost("/api/products", (ProductInput input) => Safe(() => store.AddProduct(input)));
app.MapPut("/api/products/{id:int}", (int id, ProductInput input) => Safe(() => store.UpdateProduct(id, input)));
app.MapDelete("/api/products/{id:int}", (int id) => Safe(() => store.DisableProduct(id)));
app.MapPost("/api/sales", (SaleInput input) => Safe(() => store.Sell(input)));
app.MapGet("/api/sales", () => Results.Ok(store.Sales()));
app.MapGet("/api/reports/top-products", () => Results.Ok(store.TopProducts()));
app.Run();

static IResult Safe(Func<object> action)
{
    try { return Results.Ok(action()); }
    catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
    catch (InvalidOperationException e) { return Results.Conflict(new { error = e.Message }); }
    catch (SqliteException e) when (e.SqliteErrorCode == 19) { return Results.Conflict(new { error = "Duplicate value or invalid reference." }); }
}

public record CategoryInput(string Name);
public record ProductInput(string Sku, string Name, int CategoryId, decimal Price, int Stock, int ReorderLevel);
public record SaleLine(int ProductId, int Quantity);
public record SaleInput(string Cashier, string PaymentMethod, List<SaleLine> Items);

public sealed class MarketStore(string connectionString)
{
    private SqliteConnection Open()
    {
        var c = new SqliteConnection(connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return c;
    }
    private static SqliteCommand Cmd(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        var cmd = c.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key,value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    public void Initialize()
    {
        using var c = Open();
        using var cmd = Cmd(c, """
        CREATE TABLE IF NOT EXISTS Categories(Id INTEGER PRIMARY KEY, Name TEXT NOT NULL UNIQUE);
        CREATE TABLE IF NOT EXISTS Products(Id INTEGER PRIMARY KEY, Sku TEXT NOT NULL UNIQUE, Name TEXT NOT NULL,
          CategoryId INTEGER NOT NULL REFERENCES Categories(Id), PriceCents INTEGER NOT NULL CHECK(PriceCents >= 0),
          Stock INTEGER NOT NULL CHECK(Stock >= 0), ReorderLevel INTEGER NOT NULL CHECK(ReorderLevel >= 0), Active INTEGER NOT NULL DEFAULT 1);
        CREATE TABLE IF NOT EXISTS Sales(Id INTEGER PRIMARY KEY, CreatedAt TEXT NOT NULL, Cashier TEXT NOT NULL,
          PaymentMethod TEXT NOT NULL, TotalCents INTEGER NOT NULL CHECK(TotalCents >= 0));
        CREATE TABLE IF NOT EXISTS SaleLines(Id INTEGER PRIMARY KEY, SaleId INTEGER NOT NULL REFERENCES Sales(Id),
          ProductId INTEGER NOT NULL REFERENCES Products(Id), Quantity INTEGER NOT NULL CHECK(Quantity > 0),
          UnitPriceCents INTEGER NOT NULL, LineTotalCents INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS IX_Products_Category ON Products(CategoryId);
        CREATE INDEX IF NOT EXISTS IX_Sales_CreatedAt ON Sales(CreatedAt);
        """); cmd.ExecuteNonQuery();
        using var count = Cmd(c,"SELECT COUNT(*) FROM Categories");
        if (Convert.ToInt32(count.ExecuteScalar()) != 0) return;
        using var tx = c.BeginTransaction();
        foreach(var name in new[]{"Groceries","Beverages","Household","Personal Care"})
        { using var q=Cmd(c,"INSERT INTO Categories(Name) VALUES($n)",("$n",name));q.Transaction=tx;q.ExecuteNonQuery(); }
        var demo = new (string sku,string name,int category,int price,int stock,int reorder)[] {
            ("GRC-001","Rice 1kg",1,18000,42,10),("GRC-002","Cooking Oil 1L",1,21000,25,8),
            ("BEV-001","Orange Juice",2,12000,30,10),("BEV-002","Mineral Water",2,3000,55,15),
            ("HOU-001","Dish Soap",3,8500,12,6),("PER-001","Hand Wash",4,9500,7,8)};
        foreach(var p in demo) {using var q=Cmd(c,"INSERT INTO Products(Sku,Name,CategoryId,PriceCents,Stock,ReorderLevel) VALUES($s,$n,$c,$p,$q,$r)",("$s",p.sku),("$n",p.name),("$c",p.category),("$p",p.price),("$q",p.stock),("$r",p.reorder));q.Transaction=tx;q.ExecuteNonQuery();}
        tx.Commit();
    }
    public object Categories()
    {using var c=Open();using var q=Cmd(c,"SELECT Id,Name FROM Categories ORDER BY Name");using var r=q.ExecuteReader();var list=new List<object>();while(r.Read())list.Add(new {id=r.GetInt32(0),name=r.GetString(1)});return list;}
    public object AddCategory(CategoryInput x)
    {if(string.IsNullOrWhiteSpace(x.Name))throw new ArgumentException("Category name is required.");using var c=Open();using var q=Cmd(c,"INSERT INTO Categories(Name) VALUES($n); SELECT last_insert_rowid();",("$n",x.Name.Trim()));return new{id=Convert.ToInt64(q.ExecuteScalar())};}
    public object Products(string? search=null,int? categoryId=null)
    {using var c=Open();using var q=Cmd(c,"""SELECT p.Id,p.Sku,p.Name,c.Name,p.CategoryId,p.PriceCents,p.Stock,p.ReorderLevel FROM Products p JOIN Categories c ON c.Id=p.CategoryId WHERE p.Active=1 AND ($search='' OR p.Name LIKE $like OR p.Sku LIKE $like) AND ($cat IS NULL OR p.CategoryId=$cat) ORDER BY p.Name""",("$search",search??""),("$like","%"+(search??"")+"%"),("$cat",categoryId));using var r=q.ExecuteReader();var list=new List<object>();while(r.Read())list.Add(new{id=r.GetInt32(0),sku=r.GetString(1),name=r.GetString(2),category=r.GetString(3),categoryId=r.GetInt32(4),price=r.GetInt64(5)/100m,stock=r.GetInt32(6),reorderLevel=r.GetInt32(7)});return list;}
    private static long Cents(decimal value) {if(value<0 || decimal.Round(value,2)!=value || value>100000000)throw new ArgumentException("Enter a valid nonnegative price with at most two decimals.");return checked((long)(value*100));}
    private static void Validate(ProductInput x){if(string.IsNullOrWhiteSpace(x.Name)||string.IsNullOrWhiteSpace(x.Sku)||x.Stock<0||x.ReorderLevel<0||x.CategoryId<=0)throw new ArgumentException("Invalid product details.");Cents(x.Price);}
    public object AddProduct(ProductInput x)
    {Validate(x);using var c=Open();using var q=Cmd(c,"INSERT INTO Products(Sku,Name,CategoryId,PriceCents,Stock,ReorderLevel) VALUES($s,$n,$c,$p,$q,$r); SELECT last_insert_rowid();",("$s",x.Sku.Trim()),("$n",x.Name.Trim()),("$c",x.CategoryId),("$p",Cents(x.Price)),("$q",x.Stock),("$r",x.ReorderLevel));return new{id=Convert.ToInt64(q.ExecuteScalar())};}
    public object UpdateProduct(int id,ProductInput x)
    {Validate(x);using var c=Open();using var q=Cmd(c,"UPDATE Products SET Sku=$s,Name=$n,CategoryId=$c,PriceCents=$p,Stock=$q,ReorderLevel=$r WHERE Id=$id AND Active=1",("$s",x.Sku.Trim()),("$n",x.Name.Trim()),("$c",x.CategoryId),("$p",Cents(x.Price)),("$q",x.Stock),("$r",x.ReorderLevel),("$id",id));if(q.ExecuteNonQuery()==0)throw new ArgumentException("Product not found.");return new{updated=true};}
    public object DisableProduct(int id)
    {using var c=Open();using var q=Cmd(c,"UPDATE Products SET Active=0 WHERE Id=$id",("$id",id));return new{updated=q.ExecuteNonQuery()>0};}
    public object Sell(SaleInput sale)
    {
        if(string.IsNullOrWhiteSpace(sale.Cashier)||sale.Cashier.Length>80||sale.PaymentMethod is not ("Cash" or "Card")||sale.Items is null||sale.Items.Count==0||sale.Items.Count>100||sale.Items.Any(x=>x.Quantity<=0||x.Quantity>10000)||sale.Items.GroupBy(x=>x.ProductId).Any(g=>g.Count()>1))throw new ArgumentException("Invalid sale or duplicate product lines.");
        using var c=Open();using var tx=c.BeginTransaction(System.Data.IsolationLevel.Serializable);
        try {
            var lines=new List<(int id,int qty,long price,long total)>();long total=0;
            foreach(var item in sale.Items){using var lookup=Cmd(c,"SELECT PriceCents,Stock FROM Products WHERE Id=$id AND Active=1",("$id",item.ProductId));lookup.Transaction=tx;using var r=lookup.ExecuteReader();if(!r.Read())throw new ArgumentException("Product not found.");long price=r.GetInt64(0);int stock=r.GetInt32(1);if(stock<item.Quantity)throw new InvalidOperationException("Insufficient stock for product #"+item.ProductId);long line=checked(price*item.Quantity);total=checked(total+line);lines.Add((item.ProductId,item.Quantity,price,line));}
            using(var insert=Cmd(c,"INSERT INTO Sales(CreatedAt,Cashier,PaymentMethod,TotalCents) VALUES($date,$cashier,$method,$total); SELECT last_insert_rowid();",("$date",DateTimeOffset.UtcNow.ToString("O",CultureInfo.InvariantCulture)),("$cashier",sale.Cashier.Trim()),("$method",sale.PaymentMethod),("$total",total))){insert.Transaction=tx;long saleId=Convert.ToInt64(insert.ExecuteScalar());
            foreach(var line in lines){using var stock=Cmd(c,"UPDATE Products SET Stock=Stock-$qty WHERE Id=$id AND Stock >= $qty AND Active=1",("$qty",line.qty),("$id",line.id));stock.Transaction=tx;if(stock.ExecuteNonQuery()!=1)throw new InvalidOperationException("Stock changed; retry the sale.");using var item=Cmd(c,"INSERT INTO SaleLines(SaleId,ProductId,Quantity,UnitPriceCents,LineTotalCents) VALUES($sid,$pid,$qty,$price,$total)",("$sid",saleId),("$pid",line.id),("$qty",line.qty),("$price",line.price),("$total",line.total));item.Transaction=tx;item.ExecuteNonQuery();}
            tx.Commit();return new{id=saleId,total=total/100m};}
        }catch{tx.Rollback();throw;}
    }
    public object Sales()
    {using var c=Open();using var q=Cmd(c,"SELECT Id,CreatedAt,Cashier,PaymentMethod,TotalCents FROM Sales ORDER BY Id DESC LIMIT 100");using var r=q.ExecuteReader();var list=new List<object>();while(r.Read())list.Add(new{id=r.GetInt64(0),createdAt=r.GetString(1),cashier=r.GetString(2),paymentMethod=r.GetString(3),total=r.GetInt64(4)/100m});return list;}
    public object Dashboard()
    {using var c=Open();using var q=Cmd(c,"SELECT (SELECT COUNT(*) FROM Products WHERE Active=1),(SELECT COUNT(*) FROM Products WHERE Active=1 AND Stock<=ReorderLevel),(SELECT COUNT(*) FROM Sales),(SELECT COALESCE(SUM(TotalCents),0) FROM Sales)");using var r=q.ExecuteReader();r.Read();return new{products=r.GetInt64(0),lowStock=r.GetInt64(1),sales=r.GetInt64(2),revenue=r.GetInt64(3)/100m};}
    public object TopProducts()
    {using var c=Open();using var q=Cmd(c,"SELECT p.Name,COALESCE(SUM(l.Quantity),0),COALESCE(SUM(l.LineTotalCents),0) FROM Products p LEFT JOIN SaleLines l ON l.ProductId=p.Id GROUP BY p.Id ORDER BY 2 DESC,p.Name LIMIT 10");using var r=q.ExecuteReader();var list=new List<object>();while(r.Read())list.Add(new{name=r.GetString(0),quantity=r.GetInt64(1),revenue=r.GetInt64(2)/100m});return list;}
}
