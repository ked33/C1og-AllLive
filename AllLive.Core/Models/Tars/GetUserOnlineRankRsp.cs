using Tup.Tars;

namespace AllLive.Core.Models.Tars
{
    public class GetUserOnlineRankRsp : TarsStruct
    {
        public string sMsg { get; set; } = ""; // tag 0
        public int iTotal { get; set; } = 0; // tag 1

        public override void ReadFrom(TarsInputStream _is)
        {
            sMsg = _is.Read(sMsg, 0, isRequire: false);
            iTotal = _is.Read(iTotal, 1, isRequire: false);
        }

        public override void WriteTo(TarsOutputStream _os)
        {
            _os.Write(sMsg, 0);
            _os.Write(iTotal, 1);
        }
    }
}
