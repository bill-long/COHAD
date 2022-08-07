using Web.Repository;

namespace Web.Controllers
{
    public class PaymentTransactionController
    {
        private readonly CohadWebDbContext _dbContext;

        public PaymentTransactionController(CohadWebDbContext dbContext)
        {
            _dbContext = dbContext;
        }
    }
}
