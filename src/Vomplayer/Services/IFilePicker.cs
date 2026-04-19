using System.Threading.Tasks;

namespace Vomplayer.Services;

public interface IFilePicker
{
    Task<string?> PickVideoFileAsync(string title);
}
