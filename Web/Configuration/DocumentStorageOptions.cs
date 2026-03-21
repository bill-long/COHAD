namespace Web.Configuration
{
    public class DocumentStorageOptions
    {
        public string ConnectionString { get; set; }

        public string ContainerName { get; set; } = "resident-documents";

        public long MaxUploadBytes { get; set; } = 104857600; // 100 MB
    }
}
