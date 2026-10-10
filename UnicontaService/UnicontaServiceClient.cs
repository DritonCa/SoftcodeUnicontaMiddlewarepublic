// Wraps the Uniconta SDK, which is not nullable-annotated; scope nullable off here
// so the rest of the codebase can stay nullable-strict (build runs with /warnaserror).
#nullable disable

using SoftcodeUnicontaMiddleware.Models.Common;
using SoftcodeUnicontaMiddleware.Models.Dtos;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Uniconta.API.DebtorCreditor;
using Uniconta.API.Service;
using Uniconta.API.System;
using Uniconta.ClientTools.DataModel;
using Uniconta.Common;
using Uniconta.Common.User;
using Uniconta.DataModel;

namespace SoftcodeUnicontaMiddleware.UnicontaService
{
    public class UnicontaServiceClient
    {
        private readonly IMemoryCache _cache;
        private readonly string _username;
        private readonly string _password;
        private readonly Guid _apiKey;

        private Session _session;
        private Company _company;

        public UnicontaServiceClient(
            string username,
            string password,
            string apiKey,
            IMemoryCache cache)
        {
            _username = username;
            _password = password;
            _apiKey = Guid.Parse(apiKey);
            _cache = cache;
        }

        public int CompanyId
        {
            get
            {
                if (_company == null)
                    throw new InvalidOperationException("Company not initialized");

                return _company.CompanyId;
            }
        }

        public async Task InitializeAsync()
        {
            var connection = new UnicontaConnection(APITarget.Live);
            _session = new Session(connection);

            var result = await _session.LoginAsync(
                _username,
                _password,
                LoginType.HTML,
                _apiKey
            );

            if (result != ErrorCodes.Succes)
                throw new Exception($"Uniconta login failed: {result}");

            var companies = await _session.GetCompanies();
            var companyId = _session.User._DefaultCompany != 0
                ? _session.User._DefaultCompany
                : companies[0].CompanyId;

            _company = await _session.OpenCompany(companyId, true);
        }

        private void EnsureInit()
        {
            if (_session == null || _company == null)
                throw new InvalidOperationException("Uniconta session not initialized");
        }

        private string TenantKey => _company.CompanyId.ToString();

        // ---------------- VERSIONING ----------------

        private static string ComputeHash(IEnumerable<string> values)
        {
            using var sha = SHA256.Create();
            var joined = string.Join("|", values);
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(joined));
            return Convert.ToHexString(bytes);
        }

        private static Dictionary<string, object> Extensions(params object[] sources)
        {
            var result = new Dictionary<string, object>();

            foreach (var src in sources)
            {
                if (src == null) continue;

                var fields = src.GetType()
                    .GetFields(System.Reflection.BindingFlags.Instance |
                               System.Reflection.BindingFlags.Public |
                               System.Reflection.BindingFlags.NonPublic);

                foreach (var field in fields)
                {
                    var value = field.GetValue(src);

                    if (value == null)
                        continue;

                    var type = value.GetType();

                    // ✅ Allow only safe, flat values
                    if (type.IsPrimitive ||
                        type.IsEnum ||
                        type == typeof(string) ||
                        type == typeof(decimal) ||
                        type == typeof(DateTime) ||
                        type == typeof(Guid))
                    {
                        result[field.Name] = value;
                    }
                    else
                    {
                        // Prevent recursion
                        result[field.Name] = value.ToString();
                    }
                }
            }

            return result;
        }

        // ---------------- PRODUCTS ----------------

        public PagedResponse<ProductDto> GetProductsPaged(
            int offset,
            int limit,
            bool includeDynamic)
        {
            EnsureInit();

            var q = new QueryAPI(_session, _company);

            var invItems = q.Query<InvItemClient>().Result ?? Array.Empty<InvItemClient>();
            var prodItems = q.Query<ProdItemClient>().Result ?? Array.Empty<ProdItemClient>();

            var prodMap = prodItems.ToDictionary(p => p._Item);

            var version = ComputeHash(invItems.Select(i => $"{i._Item}:{i._Blocked}"));
            var cacheKey =
                $"tenant:{TenantKey}:products:v{version}:{offset}:{limit}:{includeDynamic}";

            if (_cache.TryGetValue(cacheKey, out PagedResponse<ProductDto> cached))
                return cached;

            var all = invItems.Select(inv =>
            {
                prodMap.TryGetValue(inv._Item, out var prod);

                var dto = new ProductDto
                {
                    Sku = inv._Item,
                    Name = inv._Name,
                    Group = inv._Group,
                    SalesPrice = inv._SalesPrice1,
                    CostPrice = inv._CostPrice,
                    StockOnHand = inv._qtyOnStock,
                    StockReserved = inv._qtyReserved,
                    StockAvailable = inv._qtyOnStock - inv._qtyReserved,
                    IsStockItem = inv._ItemType == 2,
                    Unit = inv._Unit.ToString(),
                    Blocked = inv._Blocked
                };

                if (includeDynamic)
                    dto.Extensions = Extensions(inv, prod);

                return dto;
            }).ToList();

            var page = all.Skip(offset).Take(limit).ToList();

            var result = new PagedResponse<ProductDto>
            {
                Items = page,
                Offset = offset,
                Limit = limit,
                Total = all.Count
            };

            _cache.Set(cacheKey, result);
            return result;
        }

        public ProductDto GetProductBySku(string sku, bool includeDynamic)
        {
            EnsureInit();

            var q = new QueryAPI(_session, _company);

            var inv = q.Query<InvItemClient>().Result
                ?.FirstOrDefault(i => i._Item == sku);

            if (inv == null)
                return null;

            var prod = q.Query<ProdItemClient>().Result
                ?.FirstOrDefault(p => p._Item == sku);

            var dto = new ProductDto
            {
                Sku = inv._Item,
                Name = inv._Name,
                Group = inv._Group,
                SalesPrice = inv._SalesPrice1,
                CostPrice = inv._CostPrice,
                StockOnHand = inv._qtyOnStock,
                StockReserved = inv._qtyReserved,
                StockAvailable = inv._qtyOnStock - inv._qtyReserved,
                IsStockItem = inv._ItemType == 2,
                Unit = inv._Unit.ToString(),
                Blocked = inv._Blocked
            };

            if (includeDynamic)
                dto.Extensions = Extensions(inv, prod);

            return dto;
        }

        // ---------------- DEBTORS ----------------

        public PagedResponse<object> GetDebtorsPaged(
            int offset,
            int limit,
            bool includeDynamic)
        {
            EnsureInit();

            var q = new QueryAPI(_session, _company);
            var debtors = q.Query<Debtor>().Result ?? Array.Empty<Debtor>();

            var version = ComputeHash(debtors.Select(d => $"{d._Account}:{d._Blocked}"));

            var cacheKey =
                $"tenant:{TenantKey}:debtors:v{version}:{offset}:{limit}:{includeDynamic}";

            if (_cache.TryGetValue(cacheKey, out PagedResponse<object> cached))
                return cached;

            var all = debtors
                .Select(d =>
                    includeDynamic
                        ? (object)UnicontaEntitySerializer.Serialize(d)
                        : new
                        {
                            Account = d._Account,
                            Name = d._Name,
                            Email = d._ContactEmail,
                            Phone = d._Phone,
                            Balance = d._CurBalance,
                            Blocked = d._Blocked
                        })
                .ToList();

            var page = all.Skip(offset).Take(limit).ToList();

            var result = new PagedResponse<object>
            {
                Items = page,
                Offset = offset,
                Limit = limit,
                Total = all.Count
            };

            _cache.Set(cacheKey, result);
            return result;
        }

        // ✅ SINGLE DEBTOR (NEW)
        public DebtorDto GetDebtorByAccount(string account, bool includeDynamic)
        {
            EnsureInit();

            var q = new QueryAPI(_session, _company);

            var debtor = q.Query<Debtor>().Result
                ?.FirstOrDefault(d => d._Account == account);

            if (debtor == null)
                return null;

            return MapDebtor(debtor, includeDynamic);
        }

        private DebtorDto MapDebtor(Debtor d, bool includeDynamic)
        {
            var dto = new DebtorDto
            {
                Account = d._Account,
                Name = d._Name,
                Group = d._Group,
                Address1 = d._Address1,
                Address2 = d._Address2,
                ZipCode = d._ZipCode,
                City = d._City,
                CountryCode = d._Country.ToString(),
                ContactPerson = d._ContactPerson,
                Email = d._ContactEmail,
                Phone = d._Phone,
                Mobile = d._MobilPhone,
                PaymentTerms = d._Payment,
                PaymentMethod = (int)d._PaymentMethod,
                CreditMax = d._CreditMax,
                Balance = d._CurBalance,
                Overdue = d._Overdue

            };

            if (includeDynamic)
                dto.Extensions = Extensions(d);

            return dto;
        }

        // ---------------- ORDERS ----------------

        public async Task<Debtor[]> GetAllDebtorsAsync()
        {
            EnsureInit();
            var q = new QueryAPI(_session, _company);
            return await q.Query<Debtor>() ?? Array.Empty<Debtor>();
        }

        public async Task<ErrorCodes> CreateDebtorAsync(DebtorClient debtor)
        {
            EnsureInit();
            var crud = new CrudAPI(_session, _company);
            return await crud.Insert(debtor);
        }

        public async Task<ErrorCodes> CreateOrderHeaderAsync(DebtorOrderClient order)
        {
            EnsureInit();
            var crud = new CrudAPI(_session, _company);
            return await crud.Insert(order);
        }

        public async Task<ErrorCodes> CreateOrderLineAsync(DebtorOrderLineClient line)
        {
            EnsureInit();
            var crud = new CrudAPI(_session, _company);
            return await crud.Insert(line);
        }

        public async Task<InvoicePostingResult> PostInvoiceAsync(DebtorOrderClient order, DebtorOrderLineClient[] lines)
        {
            EnsureInit();
            var crud = new CrudAPI(_session, _company);
            var iapi = new InvoiceAPI(crud);
            return await iapi.PostInvoice(order, lines, DateTime.Now, 0, false);
        }

        /// <summary>
        /// The latest invoice PDF for a webshop order number.
        ///
        /// The middleware creates the sales order with the webshop's order number as
        /// _OrderNumber, and the posted invoice keeps it in its own OrderNumber field, so an
        /// invoice can be found even when the webshop never stored the invoice number.
        /// With several invoices on one order (partial invoicing, a credit note followed by
        /// a new invoice) the HIGHEST, i.e. latest, number wins.
        ///
        /// Returns both the PDF and the number so the caller can store it. Null when there
        /// is no invoice or Uniconta cannot render it.
        /// </summary>
        public async Task<(byte[] Pdf, int InvoiceNumber)?> GetInvoicePdfByOrderAsync(int orderNumber)
        {
            EnsureInit();

            var crud = new CrudAPI(_session, _company);

            var filter = new[]
            {
                PropValuePair.GenereteWhereElements(
                    "OrderNumber", typeof(int), orderNumber.ToString(CultureInfo.InvariantCulture))
            };

            var found = await crud.Query<DebtorInvoiceClient>(filter);
            var invoice = found?.OrderByDescending(f => f._InvoiceNumber).FirstOrDefault();

            if (invoice == null)
                return null;

            var iapi = new InvoiceAPI(crud);
            var pdf = await iapi.GetDebtorInvoicePdf(
                invoice._DCAccount, invoice._Date, (int)invoice._InvoiceNumber, true);

            return (pdf != null && pdf.Length > 0)
                ? (pdf, (int)invoice._InvoiceNumber)
                : null;
        }

        /// <summary>
        /// The invoice PDF as Uniconta renders it, looked up by invoice number.
        ///
        /// Payment terms, due date, requisition and references are filled in by the
        /// bookkeeper in Uniconta, so the ERP's own document is the one to show; a second
        /// document drawn by the webshop would sooner or later disagree with it.
        /// Generate=true asks Uniconta to render the PDF when none is stored yet.
        /// Returns null rather than throwing, so the caller can fall back to its own document.
        /// </summary>
        public async Task<byte[]?> GetInvoicePdfAsync(int invoiceNumber)
        {
            EnsureInit();

            var crud = new CrudAPI(_session, _company);

            var filter = new[]
            {
                PropValuePair.GenereteWhereElements(
                    "InvoiceNumber", typeof(int), invoiceNumber.ToString(CultureInfo.InvariantCulture))
            };

            var found = await crud.Query<DebtorInvoiceClient>(filter);
            var invoice = found?.FirstOrDefault();

            if (invoice == null)
                return null;

            var iapi = new InvoiceAPI(crud);
            var pdf = await iapi.GetDebtorInvoicePdf(
                invoice._DCAccount, invoice._Date, invoiceNumber, true);

            // An empty byte[] is not an error in Uniconta, but it is not an invoice
            // either; the caller must be able to tell "got something" from "got nothing".
            return (pdf != null && pdf.Length > 0) ? pdf : null;
        }

        public async Task<DebtorOrderClient?> GetOrderByNumberAsync(int orderNumber)
        {
            EnsureInit();
            var crud = new CrudAPI(_session, _company);
            var order = new DebtorOrderClient { _OrderNumber = orderNumber };
            var result = await crud.Read(order);
            return result == ErrorCodes.Succes ? order : null;
        }

        public async Task<DebtorOrderLineClient[]> GetOrderLinesAsync(DebtorOrderClient order)
        {
            EnsureInit();
            var q = new QueryAPI(_session, _company);
            return await q.Query<DebtorOrderLineClient>(order) ?? Array.Empty<DebtorOrderLineClient>();
        }

        /// <summary>
        /// Deletes a sales order in Uniconta.
        ///
        /// The lines are deleted FIRST; deleting only the header would leave orphaned line
        /// rows behind. A missing order is not an error: there is simply nothing to delete.
        /// </summary>
        public async Task<(bool Existed, bool Deleted, string? Error)> DeleteOrderAsync(int orderNumber)
        {
            EnsureInit();
            var crud = new CrudAPI(_session, _company);

            var order = new DebtorOrderClient { _OrderNumber = orderNumber };
            if (await crud.Read(order) != ErrorCodes.Succes)
                return (false, false, null);

            foreach (var line in await GetOrderLinesAsync(order))
            {
                var lineResult = await crud.Delete(line);
                if (lineResult != ErrorCodes.Succes)
                    return (true, false, $"Order line could not be deleted: {lineResult}");
            }

            var svar = await crud.Delete(order);
            return svar == ErrorCodes.Succes
                ? (true, true, null)
                : (true, false, svar.ToString());
        }

        public async Task<ErrorCodes> UpdateOrderHeaderAsync(DebtorOrderClient order)
        {
            EnsureInit();
            var crud = new CrudAPI(_session, _company);
            return await crud.Update(order);
        }
    }
}
