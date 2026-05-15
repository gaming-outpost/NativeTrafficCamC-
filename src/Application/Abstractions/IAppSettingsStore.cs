using CoastalCommandCenter.Models;

namespace CoastalCommandCenter.Application.Abstractions;

public interface IAppSettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}
