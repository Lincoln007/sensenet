﻿using System.Text.RegularExpressions;

namespace SenseNet.Packaging
{
    public class PackageParameter
    {
        internal static readonly string ParameterRegex = @"^([\w_]+):";
        
        public string PropertyName { get; private set; }
        public string Value { get; private set; }

        public static bool IsValidParameter(string parameter)
        {
            return Regex.Match(parameter, ParameterRegex, RegexOptions.IgnoreCase).Success;
        }
        internal static PackageParameter Parse(string parameter)
        {
            var pkgParameter = new PackageParameter { PropertyName = string.Empty, Value = string.Empty };
            var match = Regex.Match(parameter, ParameterRegex, RegexOptions.IgnoreCase);

            pkgParameter.PropertyName = match.Value.TrimEnd(':');

            if (match.Value.Length < parameter.Length)
                pkgParameter.Value = parameter.Substring(match.Value.Length);

            return pkgParameter;
        }
    }
}
