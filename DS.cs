using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace CashPlace
{
    public class DS //: IDisposable
    {
        private string _url;
        private int _timeout = 100000; // 100 секунд по умолчанию
        

        public DS()
        {
            //_webClient = new WebClient();
            //_webClient.Headers.Add("Content-Type", "text/xml; charset=utf-8");
            //_webClient.Headers.Add("SOAPAction", "http://tempuri.org/");
        }

        public string Url
        {
            get => _url;
            set => _url = value;
        }

        public int Timeout
        {
            get => _timeout;
            set => _timeout = value;
        }        

        private string ExecuteSoapRequest(string soapEnvelope, string operationName)
        {
            if (string.IsNullOrEmpty(_url))
            {
                throw new InvalidOperationException("URL не установлен. Установите свойство Url перед вызовом методов.");
            }

            try
            {
                // Создаем HttpWebRequest для поддержки Timeout
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(_url);
                request.Method = "POST";
                request.ContentType = "text/xml; charset=utf-8";
                request.Headers.Add("SOAPAction", "http://tempuri.org/" + operationName);
                request.Timeout = _timeout;

                // ==========================================
                // ВОТ ЭТИ ТРИ СТРОКИ СПАСУТ ПЕРВЫЙ ЗАПРОС:
                // ==========================================
                request.Proxy = WebRequest.GetSystemWebProxy();
                request.Credentials = CredentialCache.DefaultNetworkCredentials;
                request.PreAuthenticate = true;

                // Записываем SOAP envelope
                byte[] data = Encoding.UTF8.GetBytes(soapEnvelope);
                request.ContentLength = data.Length;

                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(data, 0, data.Length);
                }

                // Получаем ответ
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch (WebException ex)
            {
                if (ex.Response != null)
                {
                    using (HttpWebResponse response = (HttpWebResponse)ex.Response)
                    using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                    {
                        string errorContent = reader.ReadToEnd();
                        throw new InvalidOperationException($"SOAP request failed: {response.StatusCode}. Content: {errorContent}", ex);
                    }
                }
                throw new InvalidOperationException($"SOAP request failed: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Error executing SOAP request {operationName}: {ex.Message}", ex);
            }
        }

        // Добавьте этот метод в класс DS (рядом со старым синхронным ExecuteSoapRequest)

        private async Task<string> ExecuteSoapRequestAsync(string soapEnvelope, string operationName)
        {
            if (string.IsNullOrEmpty(_url))
            {
                throw new InvalidOperationException("URL не установлен.");
            }

            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(_url);
                request.Method = "POST";
                request.ContentType = "text/xml; charset=utf-8";
                request.Headers.Add("SOAPAction", "http://tempuri.org/" + operationName);
                request.Timeout = _timeout;
                
                byte[] data = Encoding.UTF8.GetBytes(soapEnvelope);
                request.ContentLength = data.Length;

                // Асинхронная запись запроса
                using (Stream stream = await request.GetRequestStreamAsync())
                {
                    await stream.WriteAsync(data, 0, data.Length);
                }

                // Асинхронное чтение ответа
                using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync())
                {
                    using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                    {
                        return await reader.ReadToEndAsync();
                    }
                }
            }
            catch (WebException ex)
            {
                if (ex.Response != null)
                {
                    using (HttpWebResponse response = (HttpWebResponse)ex.Response)
                    using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                    {
                        string errorContent = reader.ReadToEnd();
                        throw new InvalidOperationException($"SOAP request failed: {response.StatusCode}. Content: {errorContent}", ex);
                    }
                }
                throw new InvalidOperationException($"SOAP request failed: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Error executing SOAP request {operationName}: {ex.Message}", ex);
            }
        }

        private T ParseSoapResponse<T>(string xmlResponse, string operationName)
        {
            try
            {
                XmlDocument xmlDoc = new XmlDocument();
                xmlDoc.LoadXml(xmlResponse);

                // Находим тело SOAP ответа
                XmlNamespaceManager ns = new XmlNamespaceManager(xmlDoc.NameTable);
                ns.AddNamespace("soap", "http://schemas.xmlsoap.org/soap/envelope/");
                ns.AddNamespace("temp", "http://tempuri.org/");

                XmlNode resultNode = xmlDoc.SelectSingleNode($"//temp:{operationName}Result", ns);
                if (resultNode == null)
                {
                    // Пробуем альтернативный путь
                    resultNode = xmlDoc.SelectSingleNode($"//{operationName}Result");

                    // Если не нашли, ищем в SOAP Body
                    if (resultNode == null)
                    {
                        XmlNode bodyNode = xmlDoc.SelectSingleNode("//soap:Body", ns);
                        if (bodyNode != null && bodyNode.FirstChild != null)
                        {
                            // Берем первый дочерний узел Body
                            XmlNode operationNode = bodyNode.FirstChild;
                            if (operationNode.FirstChild != null)
                            {
                                resultNode = operationNode.FirstChild;
                            }
                        }
                    }
                }

                if (resultNode != null)
                {
                    string value = resultNode.InnerText;

                    if (typeof(T) == typeof(string))
                    {
                        return (T)(object)value;
                    }
                    else if (typeof(T) == typeof(bool))
                    {
                        bool result = bool.TryParse(value, out bool val) ? val : false;
                        return (T)(object)result;
                    }
                    else if (typeof(T) == typeof(DateTime))
                    {
                        DateTime result = DateTime.TryParse(value, out DateTime val) ? val : DateTime.MinValue;
                        return (T)(object)result;
                    }
                    else if (typeof(T) == typeof(string[]))
                    {
                        // Простая обработка массива строк
                        string[] array = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        return (T)(object)array;
                    }
                    else if (typeof(T) == typeof(byte[]))
                    {
                        // Обработка base64Binary
                        try
                        {
                            byte[] bytes = Convert.FromBase64String(value);
                            return (T)(object)bytes;
                        }
                        catch
                        {
                            // Если не base64, возвращаем как байты строки
                            return (T)(object)Encoding.UTF8.GetBytes(value);
                        }
                    }
                }

                // Если не нашли результат, возвращаем всю XML как строку для типа string
                if (typeof(T) == typeof(string))
                {
                    return (T)(object)xmlResponse;
                }

                throw new InvalidOperationException($"Не удалось распарсить ответ от операции {operationName}");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Ошибка при разборе XML ответа от {operationName}: {ex.Message}", ex);
            }
        }

        // Основные методы (все 26 методов из оригинала)

        public string HelloWorld()
        {
            string soapEnvelope = @"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <HelloWorld xmlns=""http://tempuri.org/"" />
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "HelloWorld");
            return ParseSoapResponse<string>(response, "HelloWorld");
        }

        public bool ServiceIsWorker()
        {
            string soapEnvelope = @"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <ServiceIsWorker xmlns=""http://tempuri.org/"" />
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "ServiceIsWorker");
            return ParseSoapResponse<bool>(response, "ServiceIsWorker");
        }

        public string[] GetTypesCard()
        {
            string soapEnvelope = @"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <GetTypesCard xmlns=""http://tempuri.org/"" />
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "GetTypesCard");
            return ParseSoapResponse<string[]>(response, "GetTypesCard");
        }

        public string GetUsers(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <GetUsers xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </GetUsers>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "GetUsers");
            return ParseSoapResponse<string>(response, "GetUsers");
        }

        public DateTime GetDateTimeServer()
        {
            string soapEnvelope = @"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <GetDateTimeServer xmlns=""http://tempuri.org/"" />
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "GetDateTimeServer");
            return ParseSoapResponse<DateTime>(response, "GetDateTimeServer");
        }

        public string SetStatusSertificat(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <SetStatusSertificat xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </SetStatusSertificat>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "SetStatusSertificat");
            return ParseSoapResponse<string>(response, "SetStatusSertificat");
        }

        public string GetDiscountClientsCount(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <GetDiscountClientsCount xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </GetDiscountClientsCount>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "GetDiscountClientsCount");
            return ParseSoapResponse<string>(response, "GetDiscountClientsCount");
        }

        public string GetDiscountClientsV8DateTime_NEW(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <GetDiscountClientsV8DateTime_NEW xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </GetDiscountClientsV8DateTime_NEW>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "GetDiscountClientsV8DateTime_NEW");
            return ParseSoapResponse<string>(response, "GetDiscountClientsV8DateTime_NEW");
        }

        public string ExistsUpdateProrgam(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <ExistsUpdateProrgam xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </ExistsUpdateProrgam>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "ExistsUpdateProrgam");
            return ParseSoapResponse<string>(response, "ExistsUpdateProrgam");
        }

        public byte[] GetUpdateProgram(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <GetUpdateProgram xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </GetUpdateProgram>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "GetUpdateProgram");
            return ParseSoapResponse<byte[]>(response, "GetUpdateProgram");
        }

        public string ExistsUpdateProrgamAvalon(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <ExistsUpdateProrgamAvalon xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </ExistsUpdateProrgamAvalon>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "ExistsUpdateProrgamAvalon");
            return ParseSoapResponse<string>(response, "ExistsUpdateProrgamAvalon");
        }

        // Добавьте этот метод в класс DS

        public async Task<string> ExistsUpdateProrgamAvalonAsync(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
        <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
            <soap:Body>
                <ExistsUpdateProrgamAvalon xmlns=""http://tempuri.org/"">
                    <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                    <data>{SecurityHelper.EscapeXml(data)}</data>
                    <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                </ExistsUpdateProrgamAvalon>
            </soap:Body>
        </soap:Envelope>";

            // МАГИЯ: Вызываем старый синхронный метод в фоновом потоке.
            // UI не зависает, а запрос уходит через рабочий HttpWebRequest!
            string response = await Task.Run(() =>
                ExecuteSoapRequest(soapEnvelope, "ExistsUpdateProrgamAvalon")
            ).ConfigureAwait(false);

            return ParseSoapResponse<string>(response, "ExistsUpdateProrgamAvalon");
        }

        public string GetUpdateProgramAvalon(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <GetUpdateProgramAvalon xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </GetUpdateProgramAvalon>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "GetUpdateProgramAvalon");
            return ParseSoapResponse<string>(response, "GetUpdateProgramAvalon");
        }

        public byte[] GetNpgsqlNew(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <GetNpgsqlNew xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </GetNpgsqlNew>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "GetNpgsqlNew");
            return ParseSoapResponse<byte[]>(response, "GetNpgsqlNew");
        }

        public byte[] GetFiles(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <GetFiles xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </GetFiles>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "GetFiles");
            return ParseSoapResponse<byte[]>(response, "GetFiles");
        }

        public byte[] GetPDP(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <GetPDP xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </GetPDP>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "GetPDP");
            return ParseSoapResponse<byte[]>(response, "GetPDP");
        }

        public byte[] GetFile(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <GetFile xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </GetFile>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "GetFile");
            return ParseSoapResponse<byte[]>(response, "GetFile");
        }

        public string GetStatusSertificat(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <GetStatusSertificat xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </GetStatusSertificat>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "GetStatusSertificat");
            return ParseSoapResponse<string>(response, "GetStatusSertificat");
        }

        public string UploadChangeStatusClients(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <UploadChangeStatusClients xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </UploadChangeStatusClients>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "UploadChangeStatusClients");
            return ParseSoapResponse<string>(response, "UploadChangeStatusClients");
        }

        public string UploadPhoneClients(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <UploadPhoneClients xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </UploadPhoneClients>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "UploadPhoneClients");
            return ParseSoapResponse<string>(response, "UploadPhoneClients");
        }

        public string UploadDeletedItems(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <UploadDeletedItems xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </UploadDeletedItems>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "UploadDeletedItems");
            return ParseSoapResponse<string>(response, "UploadDeletedItems");
        }       

        public byte[] GetDataForCasheV8JasonAvalon(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <GetDataForCasheV8JasonAvalon xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </GetDataForCasheV8JasonAvalon>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "GetDataForCasheV8JasonAvalon");
            return ParseSoapResponse<byte[]>(response, "GetDataForCasheV8JasonAvalon");
        }

        // public async Task<byte[]> GetDataForCasheV8JasonAvalonAsync(string nick_shop, string data, string scheme)
        // {
        //     string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
        //     <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
        //         <soap:Body>
        //             <GetDataForCasheV8JasonAvalon xmlns=""http://tempuri.org/"">
        //                 <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
        //                 <data>{SecurityHelper.EscapeXml(data)}</data>
        //                 <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
        //             </GetDataForCasheV8JasonAvalon>
        //         </soap:Body>
        //     </soap:Envelope>";
        //
        //     string response = await ExecuteSoapRequestAsync(soapEnvelope, "GetDataForCasheV8JasonAvalon");
        //     return ParseSoapResponse<byte[]>(response, "GetDataForCasheV8JasonAvalon");
        // }
        
        public async Task<byte[]> GetDataForCasheV8JsonAvalonAsync(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
            <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                <soap:Body>
                    <GetDataForCasheV8JasonAvalon xmlns=""http://tempuri.org/"">
                        <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                        <data>{SecurityHelper.EscapeXml(data)}</data>
                        <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                    </GetDataForCasheV8JasonAvalon>
                </soap:Body>
            </soap:Envelope>";

            string response = await ExecuteSoapRequestAsync(soapEnvelope, "GetDataForCasheV8JasonAvalon");
            return ParseSoapResponse<byte[]>(response, "GetDataForCasheV8JasonAvalon");
        }

        public bool UploadOpeningClosingShops(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <UploadOpeningClosingShops xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </UploadOpeningClosingShops>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "UploadOpeningClosingShops");
            return ParseSoapResponse<bool>(response, "UploadOpeningClosingShops");
        }

        public string GetDataForCasheV8Successfully(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <GetDataForCasheV8Successfully xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </GetDataForCasheV8Successfully>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "GetDataForCasheV8Successfully");
            return ParseSoapResponse<string>(response, "GetDataForCasheV8Successfully");
        }

        public string OnlineCasheV8Successfully(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <OnlineCasheV8Successfully xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </OnlineCasheV8Successfully>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "OnlineCasheV8Successfully");
            return ParseSoapResponse<string>(response, "OnlineCasheV8Successfully");
        }

        public byte[] GetDataForCasheV8JasonUpdateOnly(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <GetDataForCasheV8JasonUpdateOnly xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </GetDataForCasheV8JasonUpdateOnly>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "GetDataForCasheV8JasonUpdateOnly");
            return ParseSoapResponse<byte[]>(response, "GetDataForCasheV8JasonUpdateOnly");
        }

        public bool UploadCDNLogsPortionJason(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <UploadCDNLogsPortionJason xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </UploadCDNLogsPortionJason>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "UploadCDNLogsPortionJason");
            return ParseSoapResponse<bool>(response, "UploadCDNLogsPortionJason");
        }

        public bool UploadErrorLogPortionJson(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <UploadErrorLogPortionJson xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </UploadErrorLogPortionJson>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "UploadErrorLogPortionJson");
            return ParseSoapResponse<bool>(response, "UploadErrorLogPortionJson");
        }

        public bool UploadDataOnSalesPortionJson(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <UploadDataOnSalesPortionJson xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </UploadDataOnSalesPortionJson>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "UploadDataOnSalesPortionJson");
            return ParseSoapResponse<bool>(response, "UploadDataOnSalesPortionJson");
        }

        public bool UploadDataOnSalesPortionJsonAvalon(string nick_shop, string data, string scheme)
        {
            string soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                    <soap:Body>
                        <UploadDataOnSalesPortionJsonAvalon xmlns=""http://tempuri.org/"">
                            <nick_shop>{SecurityHelper.EscapeXml(nick_shop)}</nick_shop>
                            <data>{SecurityHelper.EscapeXml(data)}</data>
                            <scheme>{SecurityHelper.EscapeXml(scheme)}</scheme>
                        </UploadDataOnSalesPortionJsonAvalon>
                    </soap:Body>
                </soap:Envelope>";

            string response = ExecuteSoapRequest(soapEnvelope, "UploadDataOnSalesPortionJsonAvalon");
            return ParseSoapResponse<bool>(response, "UploadDataOnSalesPortionJsonAvalon");
        }

        //public void Dispose()
        //{
        //    //_webClient?.Dispose();
        //}
    }

    internal static class SecurityHelper
    {
        public static string EscapeXml(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            return input
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }
    }
}