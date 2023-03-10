using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Quva.Devices;

public enum DeviceType
{
    None,
    Scale,
    Card,
    Display,
    Cam,
    SpsVisu
}

[Flags]
public enum DeviceRoles
{
    None = 0,
    Entry = 1,  //'E'
    Exit = 2,   //'A'
}

public enum TransportType
{
    All,
    Truck,
    Rail
}

public enum PackagingType
{
    All,
    Bulk,
    Packaged
}

public enum ScaleUnit
{
    Ton,
    Kilogram,
    Gram
}

public static class DeviceUtils
{
    public static string UnitShort(ScaleUnit scaleUnit)
    {
        string result = scaleUnit switch
        {
            ScaleUnit.Ton => "t",
            ScaleUnit.Kilogram => "kg",
            ScaleUnit.Gram => "gr",
            _ => throw new NotImplementedException()
        };
        return result;
    }

}

public class DeviceData
{
    //für alle Geräte:
    public string DeviceCode { get; set; }
    public string Command { get; set; }
    public int ErrorNr { get; set; }
    public string ErrorText { get; set; } = string.Empty;

    public DeviceData(string deviceCode, string command)
    {
        DeviceCode = deviceCode;
        Command = command;
    }
}

