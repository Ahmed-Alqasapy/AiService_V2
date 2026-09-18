using AiService.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NPOI.HSSF.Record.Aggregates;

namespace AiService.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class WelcomeController : ControllerBase
    {

        private readonly MyDbContext _db;
        public WelcomeController(MyDbContext db)
        {
            _db = db;
        }




        [HttpPost("welcome")]
        public async Task<IActionResult> GetWelcomeMessage([FromBody] WelcomeRequest request)
        {
            if (string.IsNullOrEmpty(request?.UserId))
                return BadRequest("UserId is required");

            //// جلب بيانات المستخدم من الداتابيز (الطريقة الصحيحة 100%)
            //var user = await _db.QuestionnaireLicOwners.AsNoTracking()
            //    .FirstOrDefaultAsync(u => u.CivilId == request.UserId);

       

            var logedinUser = await _db.QuestionnaireLicOwners.AsNoTracking().FirstOrDefaultAsync(o => o.CivilId == request.UserId);


            var user = new
            {
                FullName = logedinUser.OwnerName??"",
                OwnerLicense = logedinUser.LicCivilId ?? 0,
                Lang = "ar"

            };

            // لو مفيش مستخدم → نرجع تحية عامة
            if (user == null)
            {
                return Ok(new
                {
                    content = "أهلاً بك! كيف يمكنني مساعدتك اليوم؟",
            //        quickReplies = new[]
            //        {
            //    new { actionId = "required_attachments", label = "ماهي المرفقات المطلوبة؟" },
            //    new { actionId = "contact_support", label = "تواصل مع الدعم" }
            //}
                });
            }

            var hour = DateTime.Now.Hour;
            string greeting = hour switch
            {
                >= 5 and < 12 => "صباح الخير",
                >= 12 and < 17 => "مساء الخير",
                _ => "أهلاً وسهلاً"
            };

            var nameToUse = !string.IsNullOrEmpty(user.FullName) ? user.FullName
                           
                           : "العميل";

            var lang = user.Lang ?? "ar";

            var content = lang == "ar"
                ? $"""
        مرحبًا مجددًا يا {nameToUse} 

       لديك رخصة برقم :
       {user.OwnerLicense}

       {greeting}! كيف يمكنني مساعدتك اليوم؟
       """
                : $"""
       Hello {nameToUse}! 

       You have an active license: {user.OwnerLicense}

       Good {(hour < 12 ? "morning" : "evening")}! How can I assist you today?
       """;


            var result = new
            {
                content
                //    quickReplies = new[]
                //    {
                //new { actionId = "required_attachments", label = "ماهي المرفقات المطلوبة؟" },
                //new { actionId = "contact_support", label = "تواصل مع الدعم" }
                // ممكن تضيف: طلبات متأخرة، إشعارات، عروض خاصة... إلخ
            };
            

            return Ok(result);
        }

        public class WelcomeRequest
        {
            public string UserId { get; set; }  // فقط هذا الحقل
        }
    }
}
