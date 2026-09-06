namespace PaqetFire.Core.Configuration;

public interface IPaqetConfigurationWriter
{
    string Write(PaqetProfile profile, string transportKey);
}
