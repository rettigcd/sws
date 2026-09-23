namespace Saz;

internal sealed class ExchangeRaw(int id) {
	public int Id { get; } = id;
	public byte[]? ClientRequestBytes { get; set; }
	public byte[]? ServerResponseBytes { get; set; }
	public byte[]? MetadataBytes { get; set; }
}
