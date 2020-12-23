#!/bin/bash

openssl genrsa -out Certs/client.key 2048
openssl req -new -key Certs/client.key -out Certs/client.csr -subj "/C=CN/ST=Beijing/L=Chaoyang/O=DevOps/OU=PKI/CN=localhost"

cp openssl.cnf openssl.actual.conf
# identityTemplate         = "spiffe://%s/ns/%s/sa/%s"
echo "URI = spiffe://cluster.local/ns/bookinfo/sa/bookinfo-details" >> openssl.actual.conf

openssl x509 -req -extensions v3_req -sha256 -days 365 \
    -CA Certs/ca-cert.pem -CAkey Certs/ca-key.pem -CAcreateserial -extfile openssl.actual.conf \
    -in Certs/client.csr -out Certs/client.pem
    
openssl pkcs12 -export -out Certs/client.pfx -inkey Certs/client.key -in Certs/client.pem


# name=${1:-foo}
# san="spiffe://trust-domain-$name/ns/$name/sa/$name"

# openssl genrsa -out "workload-$name-key.pem" 2048

# cat > workload.cfg <<EOF
# [req]
# distinguished_name = req_distinguished_name
# req_extensions = v3_req
# x509_extensions = v3_req
# prompt = no
# [req_distinguished_name]
# [v3_req]
# keyUsage = critical, digitalSignature, keyEncipherment
# extendedKeyUsage = serverAuth, clientAuth
# basicConstraints = critical, CA:FALSE
# subjectAltName = critical, @alt_names
# [alt_names]
# URI = $san
# EOF

# openssl req -new -key "workload-$name-key.pem" -subj "/" -out workload.csr -config workload.cfg

# openssl x509 -req -in workload.csr -CA ca-cert.pem -CAkey ca-key.pem -CAcreateserial \
# -out "workload-$name-cert.pem" -days 3650 -extensions v3_req -extfile workload.cfg

# cat cert-chain.pem >> "workload-$name-cert.pem"

# echo "Generated workload-$name-[cert|key].pem with URI SAN $san"
# openssl verify -CAfile <(cat cert-chain.pem root-cert.pem) "workload-$name-cert.pem"

# # clean temporary files
# rm ca-cert.srl workload.cfg workload.csr
