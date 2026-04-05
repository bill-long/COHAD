using System;
using Web.Models;

namespace Web.PresentationModels
{
    public class DirectoryPhoneNumber
    {
        public static DirectoryPhoneNumber FromStorageModel(PhoneNumber storedPhoneNumber)
        {
            if (!storedPhoneNumber.VisibleInDirectory)
            {
                throw new PhoneNumberHiddenFromDirectoryException();
            }

            return new DirectoryPhoneNumber(
                storedPhoneNumber.AreaCode,
                storedPhoneNumber.Prefix,
                storedPhoneNumber.LineNumber,
                storedPhoneNumber.Type
            );
        }

        private DirectoryPhoneNumber(int areaCode, int prefix, int lineNumber, string type)
        {
            AreaCode = areaCode;
            Prefix = prefix;
            LineNumber = lineNumber;
            Type = type;
        }

        public int AreaCode { get; }

        public int Prefix { get; }

        public int LineNumber { get; }

        public string Type { get; }

        public class PhoneNumberHiddenFromDirectoryException : Exception
        {
            public PhoneNumberHiddenFromDirectoryException()
                : base("Phone number is not allowed to be shown in directory.") { }
        }
    }
}
