using CoastalCommandCenter.Application.Abstractions;
using CoastalCommandCenter.Infrastructure.Persistence;

namespace CoastalCommandCenter.Infrastructure.Bootstrap;

public sealed class SqliteCameraDataBootstrapper : ICameraDataBootstrapper
{
    private readonly SqliteCameraRepository _repository;
    private readonly LegacyJsonCameraSeedLoader _seedLoader;

    public SqliteCameraDataBootstrapper(
        SqliteCameraRepository repository,
        LegacyJsonCameraSeedLoader seedLoader)
    {
        _repository = repository;
        _seedLoader = seedLoader;
    }

    public void Initialize()
    {
        _repository.InitializeSchema();

        if (_repository.HasAnyCameras())
        {
            return;
        }

        var seed = _seedLoader.LoadSeed();
        if (seed.Cameras.Count == 0)
        {
            Console.WriteLine("[SqliteCameraDataBootstrapper] No legacy camera data found to seed SQLite.");
            return;
        }

        _repository.ReplaceCatalog(seed);
        Console.WriteLine(
            $"[SqliteCameraDataBootstrapper] Seeded {_repository.DatabasePath} with {seed.Cameras.Count} cameras and {seed.Groups.Count} groups.");
    }
}
