using Grayson.Vision.Repository.Entities;
using Grayson.Vision.Repository.Interfaces;
using System.Linq;

namespace Grayson.Vision.Repository.Implementations
{
    public class LiteDbUserRepository : LiteDbRepositoryBase<UserPo>, IUserRepository
    {
        protected override string CollectionName => "users";

        public UserPo GetByUsername(string username)
        {
            return Find(x => x.Username == username).FirstOrDefault();
        }

        public bool ValidateUser(string username, string passwordHash)
        {
            var user = GetByUsername(username);
            if (user == null || user.IsLocked)
            {
                return false;
            }
            return user.PasswordHash == passwordHash;
        }

        public override bool Insert(UserPo entity)
        {
            using (var db = Core.DbContext.GetDatabase())
            {
                var col = db.GetCollection<UserPo>(CollectionName);
                // 索引优化：确保 Username 唯一/快速索引
                col.EnsureIndex(x => x.Username, true);
                return base.Insert(entity);
            }
        }
    }
}