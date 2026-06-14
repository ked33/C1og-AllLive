using Tup.Tars;

namespace AllLive.Core.Models.Tars
{
    public class GetUserOnlineRankReq : TarsStruct
    {
        public HuyaUserId tId { get; set; } = new HuyaUserId(); // tag 0
        public long lPid { get; set; } = 0; // tag 1

        public override void ReadFrom(TarsInputStream _is)
        {
            var tIdValue = _is.Read(tId, 0, isRequire: false);
            if (tIdValue != null)
            {
                tId = (HuyaUserId)tIdValue;
            }
            lPid = _is.Read(lPid, 1, isRequire: false);
        }

        public override void WriteTo(TarsOutputStream _os)
        {
            _os.Write(tId, 0);
            _os.Write(lPid, 1);
        }
    }
}
