namespace SharpLabNext.ArtifactStore;

public abstract class ArtifactStoreException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class ArtifactValidationException(string message, Exception? innerException = null)
    : ArtifactStoreException(message, innerException);

public sealed class ArtifactNotFoundException(string message)
    : ArtifactStoreException(message);

public sealed class ArtifactConflictException(string message)
    : ArtifactStoreException(message);

public sealed class ArtifactLimitExceededException(string message, Exception? innerException = null)
    : ArtifactStoreException(message, innerException);

public sealed class ArtifactCorruptedException(string message, Exception? innerException = null)
    : ArtifactStoreException(message, innerException);
