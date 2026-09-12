namespace QuotesApi.Services;

public interface ITextNormalizer
{
    string Trim(string value);
}

// Stateless and cheap, so there's no benefit to sharing one instance (Singleton) -
// registering it Transient means nobody ever has to reason about whether it's safe
// to add state to this class later, because a fresh instance is used every time.
public class TextNormalizer : ITextNormalizer
{
    public string Trim(string value) => value.Trim();
}
