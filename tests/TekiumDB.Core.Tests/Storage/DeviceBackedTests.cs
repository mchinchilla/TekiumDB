// Copyright (c) Tekium. Licensed under the MIT License.

namespace TekiumDB.Tests.Storage;

/// <summary>
/// Base de las pruebas que deben pasar igual sobre cualquier dispositivo. La clase derivada sólo
/// elige cuál; al terminar cada prueba se cierran y borran los que se hayan creado.
/// </summary>
public abstract class DeviceBackedTests : IAsyncLifetime
{
    private readonly List<TestDevice> _devices = [];

    public ValueTask InitializeAsync() => default;

    public async ValueTask DisposeAsync()
    {
        foreach (var device in _devices)
        {
            await device.DestroyAsync();
        }

        GC.SuppressFinalize(this);
    }

    private protected TestDevice NewDevice()
    {
        var device = CreateDevice();
        _devices.Add(device);
        return device;
    }

    private protected abstract TestDevice CreateDevice();
}
