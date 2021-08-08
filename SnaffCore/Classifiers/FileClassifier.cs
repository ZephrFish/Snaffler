﻿using SnaffCore.Classifiers;
using SnaffCore.Concurrency;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using static SnaffCore.Config.Options;

namespace Classifiers
{
    public class FileClassifier
    {
        private ClassifierRule ClassifierRule { get; set; }

        public FileClassifier(ClassifierRule inRule)
        {
            this.ClassifierRule = inRule;
        }

        public bool ClassifyFile(FileInfo fileInfo)
        {
            BlockingMq Mq = BlockingMq.GetMq();
            // figure out what part we gonna look at
            string stringToMatch = null;

            switch (ClassifierRule.MatchLocation)
            {
                case MatchLoc.FileExtension:
                    stringToMatch = fileInfo.Extension;
                    // special handling to treat files named like 'thing.kdbx.bak'
                    if (stringToMatch == ".bak")
                    {
                        // strip off .bak
                        string subName = fileInfo.Name.Replace(".bak", "");
                        stringToMatch = Path.GetExtension(subName);
                        // if this results in no file extension, put it back.
                        if (stringToMatch == "")
                        {
                            stringToMatch = ".bak";
                        }
                    }
                    // this is insane that i have to do this but apparently files with no extension return
                    // this bullshit
                    if (stringToMatch == "")
                    {
                        return false;
                    }
                    break;
                case MatchLoc.FileName:
                    stringToMatch = fileInfo.Name;
                    break;
                case MatchLoc.FilePath:
                    stringToMatch = fileInfo.FullName;
                    break;
                case MatchLoc.FileLength:
                    if (!SizeMatch(fileInfo))
                    {
                        return false;
                    }
                    else break;
                default:
                    Mq.Error("You've got a misconfigured file classifier rule named " + ClassifierRule.RuleName + ".");
                    return false;
            }

            TextResult textResult = null;

            if (!String.IsNullOrEmpty(stringToMatch))
            {
                TextClassifier textClassifier = new TextClassifier(ClassifierRule);
                // check if it matches
                textResult = textClassifier.TextMatch(stringToMatch);
                if (textResult == null)
                {
                    // if it doesn't we just bail now.
                    return false;
                }
            }

            FileResult fileResult;
            // if it matches, see what we're gonna do with it
            switch (ClassifierRule.MatchAction)
            {
                case MatchAction.Discard:
                    // chuck it.
                    return true;
                case MatchAction.Snaffle:
                    // snaffle that bad boy
                    fileResult = new FileResult(fileInfo)
                    {
                        MatchedRule = ClassifierRule,
                        TextResult = textResult
                    };
                    Mq.FileResult(fileResult);
                    return false;
                    //return true;
                case MatchAction.CheckForKeys:
                    // do a special x509 dance
                    List<string> x509MatchReason = x509Match(fileInfo);
                    if (x509MatchReason.Count >= 0)
                    {
                        // if there were any matchreasons, cat them together...
                        string matchContext = String.Join(",", x509MatchReason);
                        // and sling the results on the queue
                        fileResult = new FileResult(fileInfo)
                        {
                            MatchedRule = ClassifierRule,
                            TextResult = new TextResult()
                            {
                                MatchContext = matchContext,
                                MatchedStrings = new List<string>() { "" }
                            }
                        };
                        Mq.FileResult(fileResult);
                    }
                    return true;
                case MatchAction.Relay:
                    // bounce it on to the next ClassifierRule
                    try
                    {
                        ClassifierRule nextRule =
                            MyOptions.ClassifierRules.First(thing => thing.RuleName == ClassifierRule.RelayTarget);

                        if (nextRule.EnumerationScope == EnumerationScope.ContentsEnumeration)
                        {
                            ContentClassifier nextContentClassifier = new ContentClassifier(nextRule);
                            nextContentClassifier.ClassifyContent(fileInfo);
                            return true;
                        }
                        else if (nextRule.EnumerationScope == EnumerationScope.FileEnumeration)
                        {
                            FileClassifier nextFileClassifier = new FileClassifier(nextRule);
                            nextFileClassifier.ClassifyFile(fileInfo);
                            return true;
                        }
                        else
                        {
                            Mq.Error("You've got a misconfigured file ClassifierRule named " + ClassifierRule.RuleName + ".");
                            return false;
                        }
                    }
                    catch (IOException e)
                    {
                        Mq.Trace(e.ToString());
                    }
                    catch (Exception e)
                    {
                        Mq.Error("You've got a misconfigured file ClassifierRule named " + ClassifierRule.RuleName + ".");
                        Mq.Trace(e.ToString());
                    }
                    return false;
                case MatchAction.EnterArchive:
                    // do a special looking inside archive files dance using
                    // https://github.com/adamhathcock/sharpcompress
                    // TODO FUUUUUCK
                    throw new NotImplementedException("Haven't implemented walking dir structures inside archives.");
                default:
                    Mq.Error("You've got a misconfigured file ClassifierRule named " + ClassifierRule.RuleName + ".");
                    return false;
            }
        }
        public bool SizeMatch(FileInfo fileInfo)
        {
            if (this.ClassifierRule.MatchLength == fileInfo.Length)
            {
                return true;
            }
            return false;
        }

        public X509Certificate2 parseCert(string certPath, string password = null)
        {
            BlockingMq Mq = BlockingMq.GetMq();
            // IT TURNS OUT THAT new X509Certificate2() actually writes a file to a temp path and if you
            // don't manage it yourself it hits 65,000 temp files and hangs.
            var tempfile = Path.Combine(Path.GetTempPath(), "Snaff-" + Guid.NewGuid());
            File.Copy(certPath, tempfile);
            X509Certificate2 parsedCert = null;

            try
            {
                if (Path.GetExtension(certPath) == ".pem")
                {
                    string pemstring = File.ReadAllText(tempfile);
                    byte[] certBuffer = Helpers.GetBytesFromPEM(pemstring, PemStringType.Certificate);
                    byte[] keyBuffer = Helpers.GetBytesFromPEM(pemstring, PemStringType.RsaPrivateKey);

                    if (certBuffer != null)
                    {
                        parsedCert = new X509Certificate2(certBuffer);
                        if (keyBuffer != null)
                        {
                            RSACryptoServiceProvider prov = Crypto.DecodeRsaPrivateKey(keyBuffer);
                            parsedCert.PrivateKey = prov;
                        }
                    }
                    else
                    {
                        Mq.Error("Failure parsing " + certPath);
                    }
                }
                else
                {
                    parsedCert = new X509Certificate2(tempfile, password);
                }
            }
            catch (Exception e)
            {
                File.Delete(tempfile);
                throw e;
            }

            File.Delete(tempfile);

            return parsedCert;
        }

        public List<string> x509Match(FileInfo fileInfo)
        {
            BlockingMq Mq = BlockingMq.GetMq();
            string certPath = fileInfo.FullName;
            List<string> matchReasons = new List<string>();
            X509Certificate2 parsedCert = null;
            bool nopwrequired = false;

            // TODO - handle if there is no private key, strip out unnecessary stuff from Certificate.cs, make work with pfx style stuff below

            try
            {
                // try to parse it, it'll throw if it needs a password
                parsedCert = parseCert(certPath);
                
                // if it parses we know we didn't need a password
                nopwrequired = true;
            }
            catch (CryptographicException e)
            {
                // if it doesn't parse that almost certainly means we need a password
                Mq.Trace(e.ToString());

                // build the list of passwords to try including the filename
                List<string> passwords = MyOptions.CertPasswords;
                passwords.Add(Path.GetFileNameWithoutExtension(fileInfo.Name));

                // try each of our very obvious passwords
                foreach (string password in MyOptions.CertPasswords)
                {
                    try
                    {
                        parsedCert = parseCert(certPath, password);
                        if (password == "")
                        {
                            matchReasons.Add("PasswordBlank");
                        }
                        else
                        {
                            matchReasons.Add("PasswordCracked: " + password);
                        }
                    }
                    catch (CryptographicException ee)
                    {
                        Mq.Trace("Password " + password + " invalid for cert file " + fileInfo.FullName + " " + ee.ToString());
                    }
                }
                if (matchReasons.Count == 0) 
                {
                    matchReasons.Add("HasPassword");
                    matchReasons.Add("LookNearbyFor.txtFiles");
                }
            }
            catch (Exception e)
            {
                Mq.Error("Unhandled exception parsing cert: " + fileInfo.FullName + " " + e.ToString());
            }

            if (parsedCert != null)
            {
                // check if it includes a private key, if not, who cares?
                if (parsedCert.HasPrivateKey)
                {
                    matchReasons.Add("HasPrivateKey");

                    if (nopwrequired) { matchReasons.Add("NoPasswordRequired"); }

                    matchReasons.Add("Subject:" + parsedCert.Subject);

                    // take a look at the extensions
                    X509ExtensionCollection extensions = parsedCert.Extensions;

                    // this feels dumb but whatever
                    foreach (X509Extension extension in extensions)
                    {
                        AsnEncodedData asndata = new AsnEncodedData(extension.Oid, extension.RawData);
                        string asndataString = asndata.Format(false);
                        if (extension.Oid.FriendlyName == "Basic Constraints")
                        {
                            if (asndataString.Contains("Subject Type=CA"))
                            {
                                matchReasons.Add("IsCACert");
                            }
                        }
                        if (extension.GetType() == typeof(X509KeyUsageExtension))
                        {
                            matchReasons.Add((extension as X509KeyUsageExtension).KeyUsages.ToString());
                        }
                        if (extension.GetType() == typeof(X509EnhancedKeyUsageExtension))
                        {
                            List<string> ekus = new List<string>();

                            X509EnhancedKeyUsageExtension ekuExtension = (X509EnhancedKeyUsageExtension)extension;
                            foreach (Oid eku in ekuExtension.EnhancedKeyUsages)
                            {
                                ekus.Add(eku.FriendlyName);
                            }
                            // include the EKUs in the info we're passing to the user
                            string ekustring = String.Join("|", ekus);
                            matchReasons.Add(ekustring);
                        };
                        if (extension.Oid.FriendlyName == "Subject Alternative Name")
                        {
                            byte[] sanbytes = extension.RawData;
                            string san = Encoding.UTF8.GetString(sanbytes, 0, sanbytes.Length);
                            matchReasons.Add(asndataString);
                        }
                    }

                    matchReasons.Add("Expiry:" + parsedCert.GetExpirationDateString());
                    matchReasons.Add("Issuer:" + parsedCert.Issuer);
                }
            }

            return matchReasons;
        }
    }

    public class RwStatus
    {
        public bool CanRead { get; set; }
        public bool CanWrite { get; set; }
    }

    public class CurrentUserSecurity
    {
        private readonly WindowsPrincipal _currentPrincipal;
        private readonly WindowsIdentity _currentUser;

        public CurrentUserSecurity()
        {
            _currentUser = WindowsIdentity.GetCurrent();
            _currentPrincipal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
        }

        public bool HasAccess(DirectoryInfo directory, FileSystemRights right)
        {
            try
            {
                // Get the collection of authorization rules that apply to the directory.
                AuthorizationRuleCollection acl = directory.GetAccessControl()
                    .GetAccessRules(true, true, typeof(SecurityIdentifier));
                return HasFileOrDirectoryAccess(right, acl);
            }

            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        public bool HasAccess(FileInfo file, FileSystemRights right)
        {
            try
            {
                // Get the collection of authorization rules that apply to the file.
                AuthorizationRuleCollection acl = file.GetAccessControl()
                    .GetAccessRules(true, true, typeof(SecurityIdentifier));

                return HasFileOrDirectoryAccess(right, acl);
            }

            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        public bool HasFileOrDirectoryAccess(FileSystemRights right,
            AuthorizationRuleCollection acl)
        {
            bool allow = false;
            bool inheritedAllow = false;
            bool inheritedDeny = false;

            for (int i = 0; i < acl.Count; i++)
            {
                FileSystemAccessRule currentRule = (FileSystemAccessRule)acl[i];
                // If the current rule applies to the current user.
                if (_currentUser.User.Equals(currentRule.IdentityReference) ||
                    _currentPrincipal.IsInRole(
                        (SecurityIdentifier)currentRule.IdentityReference))
                {
                    if (currentRule.AccessControlType.Equals(AccessControlType.Deny))
                    {
                        if ((currentRule.FileSystemRights & right) == right)
                        {
                            if (currentRule.IsInherited)
                                inheritedDeny = true;
                            else
                                // Non inherited "deny" takes overall precedence.
                                return false;
                        }
                    }
                    else if (currentRule.AccessControlType
                        .Equals(AccessControlType.Allow))
                    {
                        if ((currentRule.FileSystemRights & right) == right)
                        {
                            if (currentRule.IsInherited)
                                inheritedAllow = true;
                            else
                                allow = true;
                        }
                    }
                }
            }

            if (allow)
                // Non inherited "allow" takes precedence over inherited rules.
                return true;

            return inheritedAllow && !inheritedDeny;
        }
    }

    public class FileResult
    {
        public FileInfo FileInfo { get; set; }
        public TextResult TextResult { get; set; }
        public RwStatus RwStatus { get; set; }
        public ClassifierRule MatchedRule { get; set; }

        public FileResult(FileInfo fileInfo)
        {
            this.RwStatus = CanRw(fileInfo);
            this.FileInfo = fileInfo;
            if (MyOptions.Snaffle)
            {
                if ((MyOptions.MaxSizeToSnaffle >= fileInfo.Length) && RwStatus.CanRead)
                {
                    SnaffleFile(fileInfo, MyOptions.SnafflePath);
                }
            }
        }

        public static RwStatus CanRw(FileInfo fileInfo)
        {
            BlockingMq Mq = BlockingMq.GetMq();

            try
            {
                RwStatus rwStatus = new RwStatus { CanWrite = CanIWrite(fileInfo), CanRead = CanIRead(fileInfo) };
                return rwStatus;
            }
            catch (Exception e)
            {
                Mq.Error(e.ToString());
                return new RwStatus { CanWrite = false, CanRead = false }; ;
            }
        }

        public void SnaffleFile(FileInfo fileInfo, string snafflePath)
        {
            string sourcePath = fileInfo.FullName;
            // clean it up and normalise it a bit
            string cleanedPath = sourcePath.Replace(':', '.').Replace('$', '.').Replace("\\\\", "\\");
            //string cleanedPath = Path.GetFullPath(sourcePath.Replace(':', '.').Replace('$', '.'));
            // make the dir exist
            string snaffleFilePath = Path.Combine(snafflePath, cleanedPath);
            string snaffleDirPath = Path.GetDirectoryName(snaffleFilePath);
            Directory.CreateDirectory(snaffleDirPath);
            File.Copy(sourcePath, (Path.Combine(snafflePath, cleanedPath)), true);
        }

        public static bool CanIRead(FileInfo fileInfo)
        {
            // this will return true if file read perm is available.
            CurrentUserSecurity currentUserSecurity = new CurrentUserSecurity();

            FileSystemRights[] fsRights =
            {
                FileSystemRights.Read,
                FileSystemRights.ReadAndExecute,
                FileSystemRights.ReadData
            };

            bool readRight = false;
            foreach (FileSystemRights fsRight in fsRights)
                try
                {
                    if (currentUserSecurity.HasAccess(fileInfo, fsRight)) readRight = true;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }

            return readRight;
        }

        public static bool CanIWrite(FileInfo fileInfo)
        {
            // this will return true if write or modify or take ownership or any of those other good perms are available.
            CurrentUserSecurity currentUserSecurity = new CurrentUserSecurity();

            FileSystemRights[] fsRights =
            {
                FileSystemRights.Write,
                FileSystemRights.Modify,
                FileSystemRights.FullControl,
                FileSystemRights.TakeOwnership,
                FileSystemRights.ChangePermissions,
                FileSystemRights.AppendData,
                FileSystemRights.WriteData
            };

            bool writeRight = false;
            foreach (FileSystemRights fsRight in fsRights)
                try
                {
                    if (currentUserSecurity.HasAccess(fileInfo, fsRight)) writeRight = true;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }

            return writeRight;
        }



    }
}