using System;
using Web.Models;

namespace Web.PresentationModels
{
    public class DirectoryEmailAddress
    {
        public static DirectoryEmailAddress FromStorageModel(EmailAddress storedEmailAddress)
        {
            if (!storedEmailAddress.VisibleInDirectory)
            {
                throw new EmailAddressHiddenFromDirectoryException();
            }

            return new DirectoryEmailAddress(storedEmailAddress.Address);
        }

        public string Address { get; }

        private DirectoryEmailAddress(string emailAddress)
        {
            Address = emailAddress;
        }

        public class EmailAddressHiddenFromDirectoryException : Exception
        {
            public EmailAddressHiddenFromDirectoryException()
                : base("Email address is not allowed to be shown in directory.") { }
        }
    }
}
