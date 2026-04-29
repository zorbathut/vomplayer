using System.Threading.Tasks;

namespace Vomplayer.Services;

public interface IFilePicker
{
    Task<string?> PickVideoFileAsync(string title);
    Task<string?> PickSubtitleFileAsync(string title);
}
