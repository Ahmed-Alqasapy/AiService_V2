using AiService.Models;

namespace AiService.Interfaces
{
    public interface IOpenAIService
    {
        Task ChatWithAIAsync(string message, List<string>? history);
    }
}
