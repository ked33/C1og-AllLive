using AllLive.Core;
using Newtonsoft.Json.Linq;
using System;
using System.Threading.Tasks;

namespace AllLive.Tests
{
    public static class BilibiliRoomFilterTests
    {
        public static Task<string> RunAsync()
        {
            CheckHiddenRoomIsFiltered();
            CheckEncryptedRoomFiltering();
            CheckNormalRoomIsNotFiltered();
            CheckFlagCompatibility();
            CheckInvalidDataKeepsCurrentStatus();
            return Task.FromResult("PASS: bilibili hidden/encrypted room filter.");
        }

        private static void CheckHiddenRoomIsFiltered()
        {
            // 实测房间 13937450：隐藏 + 加密 + 未验证密码，get_info 仍返回 live_status=1。
            var data = JObject.Parse("{\"is_hidden\":true,\"is_locked\":false,\"encrypted\":true,\"pwd_verified\":false}");
            Assert(BilibiliRoomFilter.ShouldFilter(data) == true, "Hidden and encrypted room must be filtered.");
        }

        private static void CheckEncryptedRoomFiltering()
        {
            var encrypted = JObject.Parse("{\"is_hidden\":false,\"is_locked\":false,\"encrypted\":true,\"pwd_verified\":false}");
            Assert(BilibiliRoomFilter.ShouldFilter(encrypted) == true, "Encrypted room without verified password must be filtered.");

            var verified = JObject.Parse("{\"is_hidden\":false,\"is_locked\":false,\"encrypted\":true,\"pwd_verified\":true}");
            Assert(BilibiliRoomFilter.ShouldFilter(verified) == false, "Encrypted room with verified password must not be filtered.");

            var locked = JObject.Parse("{\"is_hidden\":false,\"is_locked\":true,\"encrypted\":false,\"pwd_verified\":false}");
            Assert(BilibiliRoomFilter.ShouldFilter(locked) == true, "Locked room must be filtered.");
        }

        private static void CheckNormalRoomIsNotFiltered()
        {
            var normal = JObject.Parse("{\"is_hidden\":false,\"is_locked\":false,\"encrypted\":false,\"pwd_verified\":false,\"live_status\":1}");
            Assert(BilibiliRoomFilter.ShouldFilter(normal) == false, "Normal live room must not be filtered.");
        }

        private static void CheckFlagCompatibility()
        {
            var numeric = JObject.Parse("{\"is_hidden\":1,\"is_locked\":0,\"encrypted\":\"1\",\"pwd_verified\":\"0\"}");
            Assert(BilibiliRoomFilter.ShouldFilter(numeric) == true, "Numeric and string flags must be treated like booleans.");
        }

        private static void CheckInvalidDataKeepsCurrentStatus()
        {
            Assert(BilibiliRoomFilter.ShouldFilter(null) == null, "Missing data must return null so callers keep the current status.");
            Assert(BilibiliRoomFilter.ShouldFilter(JValue.CreateNull()) == null, "Null JSON data must return null.");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + message);
            }
        }
    }
}
