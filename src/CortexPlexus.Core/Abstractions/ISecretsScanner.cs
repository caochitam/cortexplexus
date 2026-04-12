namespace CortexPlexus.Core.Abstractions;

public interface ISecretsScanner
{
    string Sanitize(string content);
    bool ContainsSecrets(string content);
}
