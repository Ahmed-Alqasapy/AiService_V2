using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System.Security.Cryptography;
using System.Text;

namespace AiService.Filters
{
    // AdminController has no other auth (this API has none end to end). The WebForms
    // dashboard proxies through this key from its own server-side web methods so the
    // browser never sees it; anything without the exact header is rejected.
    public class AdminApiKeyFilter : IAuthorizationFilter
    {
        private readonly string _key;

        public AdminApiKeyFilter(IConfiguration cfg) =>
            _key = cfg["Admin:ApiKey"] ?? throw new InvalidOperationException("Admin:ApiKey missing");

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            var sent = context.HttpContext.Request.Headers["X-Admin-Key"].ToString();
            var ok = sent.Length == _key.Length &&
                     CryptographicOperations.FixedTimeEquals(
                         Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(_key));
            if (!ok) context.Result = new UnauthorizedResult();
        }
    }
}
