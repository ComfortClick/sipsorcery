using Org.BouncyCastle.Tls;

namespace SIPSorcery.net.DtlsSrtp
{
    public class PatchedDtlsServerProtocol : DtlsServerProtocol
    {
        protected override void ProcessCertificateVerify(ServerHandshakeState state, byte[] body, TlsHandshakeHash handshakeHash)
        { }
    }
}
