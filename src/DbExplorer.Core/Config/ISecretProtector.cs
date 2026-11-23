namespace DbExplorer.Core.Config;

public interface ISecretProtector
{
    string Protect(string plain);
    string Unprotect(string protectedValue);
}
