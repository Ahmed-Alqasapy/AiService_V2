namespace AiService.Services
{
    // Prices come from config (Pricing:<model prefix>), not code, so they can be
    // updated without a redeploy. Cost is computed once and stored on the log row,
    // so a later price change doesn't retroactively change historical cost.
    public class CostCalculator
    {
        private readonly IConfiguration _cfg;
        public CostCalculator(IConfiguration cfg) => _cfg = cfg;

        public decimal Calculate(string? model, int inTok, int outTok)
        {
            if (string.IsNullOrEmpty(model)) return 0;

            // OpenAI/Azure deployments return versioned or deployment names
            // (e.g. "gpt4o-mini-dev"), so match by longest configured prefix.
            var section = _cfg.GetSection("Pricing").GetChildren()
                              .Where(s => model.StartsWith(s.Key, StringComparison.OrdinalIgnoreCase))
                              .OrderByDescending(s => s.Key.Length)
                              .FirstOrDefault();
            if (section == null) return 0;

            var inPrice = section.GetValue<decimal>("InputPer1M");
            var outPrice = section.GetValue<decimal>("OutputPer1M");
            return Math.Round(inTok * inPrice / 1_000_000m + outTok * outPrice / 1_000_000m, 6);
        }
    }
}
